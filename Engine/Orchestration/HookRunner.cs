using System.Text;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Engine.Services;
using Railway.Engine.Services.Interfaces;

namespace Railway.Engine.Orchestration;

/// <summary>
/// Executes the pipelines subscribed to a hook event: parses the repository's pipelines, selects the
/// ones whose triggers match the event (honoring push branch/tag filters), guards each with a
/// <see cref="HookLock"/>, runs them through <see cref="PipelineRunner"/> with trigger provenance, and
/// optionally notifies. Never writes to stdout.
/// </summary>
/// <remarks>
/// Overlap policy: when the per (repo, pipeline) lock is held, a <see cref="Run"/> row with
/// <see cref="RunStatus.Skipped"/> is recorded (no job/step rows) and no pipeline is started.
/// Pipeline-level failures (parse, validation, ambiguity) are returned as
/// <see cref="HookRunResult.Error"/> results; only repository-resolution problems throw.
/// </remarks>
public sealed class HookRunner : IHookRunner
{
    private readonly IHookPipelineSource _pipelineSource;
    private readonly PipelineRunner _pipelineRunner;
    private readonly HookPaths _paths;
    private readonly ISystemCommandRunner _systemCommandRunner;
    private readonly IHookLauncher _launcher;
    private readonly RailwayDbContext _dbContext;
    private readonly INotificationService? _notificationService;

    /// <summary>
    /// Initializes a new instance of the <see cref="HookRunner"/> class.
    /// </summary>
    /// <param name="pipelineSource">Loads the repository's pipelines for trigger matching.</param>
    /// <param name="pipelineRunner">Runs the selected pipelines (trigger-aware overload).</param>
    /// <param name="paths">Resolves lock root, wrapper and log paths.</param>
    /// <param name="systemCommandRunner">Spawns detached children for <c>--detach</c>.</param>
    /// <param name="launcher">Supplies the command used to spawn detached children.</param>
    /// <param name="dbContext">Persists skipped run rows.</param>
    /// <param name="notificationService">Optional notifier; when unregistered or null, notifications are skipped.</param>
    public HookRunner(
        IHookPipelineSource pipelineSource,
        PipelineRunner pipelineRunner,
        HookPaths paths,
        ISystemCommandRunner systemCommandRunner,
        IHookLauncher launcher,
        RailwayDbContext dbContext,
        INotificationService? notificationService = null)
    {
        _pipelineSource = pipelineSource;
        _pipelineRunner = pipelineRunner;
        _paths = paths;
        _systemCommandRunner = systemCommandRunner;
        _launcher = launcher;
        _dbContext = dbContext;
        _notificationService = notificationService;
    }

    /// <inheritdoc />
    public async Task<HookRunOutcome> RunEventAsync(
        string repositoryPath,
        TriggerEvent triggerEvent,
        HookEventContext context,
        string? pipelineFile,
        bool detach,
        bool notify,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var railwayPath = RepositoryHelper.GetRailwayPathOrThrow(repositoryPath);
        var repositoryRoot = RepositoryHelper.GetRootRepositoryPath(repositoryPath)
            ?? throw new NotARailwayRepositoryException();

        var candidates = await _pipelineSource.GetPipelinesAsync(railwayPath);
        var selected = HookPipelineSelector.Select(candidates, triggerEvent, context, pipelineFile);

        var triggerKey = HookScriptHelper.GetTriggerKey(triggerEvent);
        var detail = context.GetDetail();

        if (pipelineFile is not null && selected.Count == 0)
        {
            var pipelineNameMatch = Path.GetFileName(pipelineFile);
            var exists = candidates.Any(candidate => Path.GetFileName(candidate.FileName) == pipelineNameMatch);
            var error = exists
                ? $"Pipeline '{pipelineFile}' has no matching trigger for event {HookScriptHelper.GetEventKeyword(triggerEvent)}."
                : $"Pipeline '{pipelineFile}' was not found.";
            return new HookRunOutcome(
                [new HookRunResult(pipelineFile, null, RunStatus.Failed, Skipped: false, Error: error)]);
        }

        var results = new List<HookRunResult>();

        foreach (var candidate in selected)
        {
            ct.ThrowIfCancellationRequested();

            if (detach)
            {
                results.Add(SpawnDetached(repositoryRoot, candidate, triggerEvent, context, notify));
                continue;
            }

            results.Add(await RunCandidateAsync(repositoryRoot, candidate, triggerEvent, triggerKey, detail, notify, ct));
        }

        return new HookRunOutcome(results);
    }

    private HookRunResult SpawnDetached(
        string repositoryRoot,
        HookPipelineCandidate candidate,
        TriggerEvent triggerEvent,
        HookEventContext context,
        bool notify)
    {
        try
        {
            var launcher = _launcher.GetLaunchCommand();
            var arguments = new List<string>(launcher.Arguments)
            {
                "hooks",
                "run",
                HookScriptHelper.GetEventKeyword(triggerEvent),
                "--path",
                repositoryRoot,
                "--pipeline",
                candidate.FileName,
                "--no-detach",
            };

            // The detached child re-runs selection from scratch, so the event context must be forwarded
            // (branch/tag filtering, clone detection and provenance detail all depend on it).
            AppendContextArguments(arguments, context);

            if (!notify)
            {
                arguments.Add("--no-notify");
            }

            var result = _systemCommandRunner.RunDetached(launcher.FileName, arguments, workingDirectory: repositoryRoot);

            return result.ExitCode == 0
                ? new HookRunResult(candidate.FileName, null, RunStatus.Running, Skipped: false, Error: null, Detached: true)
                : new HookRunResult(
                    candidate.FileName,
                    null,
                    RunStatus.Failed,
                    Skipped: false,
                    Error: $"Failed to spawn detached run: {result.StandardError}");
        }
        catch (Exception exception)
        {
            return new HookRunResult(candidate.FileName, null, RunStatus.Failed, Skipped: false, Error: exception.Message);
        }
    }

    /// <summary>
    /// Appends the non-empty event-context values as <c>hooks run</c> flags so the detached child
    /// reproduces the same selection and provenance as this process. Empty values are omitted, which
    /// keeps the spawned argument vector identical for context-less events (e.g. <c>post-commit</c>).
    /// </summary>
    private static void AppendContextArguments(List<string> arguments, HookEventContext context)
    {
        Append("--branch", context.Branch);
        Append("--sha", context.Sha);
        Append("--tag", context.Tag);
        Append("--remote", context.Remote);
        Append("--prev", context.PreviousSha);
        Append("--new", context.NewSha);
        Append("--flag", context.Flag);
        Append("--cron", context.Cron);

        if (context.IsSquash is { } isSquash)
        {
            arguments.Add("--is-squash");
            arguments.Add(isSquash ? "true" : "false");
        }

        void Append(string flag, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            arguments.Add(flag);
            arguments.Add(value);
        }
    }

    private async Task<HookRunResult> RunCandidateAsync(
        string repositoryRoot,
        HookPipelineCandidate candidate,
        TriggerEvent triggerEvent,
        string triggerKey,
        string? detail,
        bool notify,
        CancellationToken ct)
    {
        if (candidate.Pipeline is null)
        {
            var error = candidate.Errors.Count > 0
                ? string.Join("; ", candidate.Errors)
                : $"Pipeline '{candidate.FileName}' could not be parsed.";

            return new HookRunResult(candidate.FileName, null, RunStatus.Failed, Skipped: false, Error: error);
        }

        var pipelineSlug = _paths.GetPipelineSlug(candidate.FileName);
        var repositorySlug = _paths.GetRepositorySlug(repositoryRoot);

        HookLock? handle = null;

        try
        {
            handle = HookLock.TryAcquire(_paths.LockRoot, repositorySlug, pipelineSlug);

            if (handle is null)
            {
                var skippedRun = await InsertSkippedRunAsync(repositoryRoot, candidate, triggerKey, detail, ct);

                return new HookRunResult(candidate.FileName, skippedRun.Id, RunStatus.Skipped, Skipped: true, Error: null);
            }

            var logsDirectory = _paths.GetLogsDirectory(repositoryRoot);
            Directory.CreateDirectory(logsDirectory);

            var logPath = _paths.GetRunLogFilePath(logsDirectory, DateTimeOffset.UtcNow, candidate.FileName, triggerEvent);

            RunOutcome outcome;

            using (var sink = new FileRunEventProgress(logPath, candidate.FileName, triggerEvent, triggerKey, detail))
            {
                try
                {
                    outcome = await _pipelineRunner.RunAsync(
                        repositoryRoot,
                        candidate.FileName,
                        verbose: false,
                        sink,
                        ct,
                        new RunTrigger(triggerKey, detail));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    await NotifyAsync(
                        notify,
                        candidate.Pipeline.Notify,
                        failed: true,
                        title: $"Railway: {candidate.FileName} {HookScriptHelper.GetEventKeyword(triggerEvent)}",
                        message: $"Pipeline '{candidate.FileName}' could not run: {exception.Message}");

                    return new HookRunResult(candidate.FileName, null, RunStatus.Failed, Skipped: false, Error: exception.Message);
                }
            }

            var failed = outcome.Run.Status == RunStatus.Failed;

            await NotifyAsync(
                notify,
                candidate.Pipeline.Notify,
                failed,
                title: $"Railway: {candidate.FileName} {HookScriptHelper.GetEventKeyword(triggerEvent)}",
                message: failed
                    ? $"Pipeline '{candidate.FileName}' failed on {HookScriptHelper.GetEventKeyword(triggerEvent)}{FormatDetailSuffix(detail)}."
                    : $"Pipeline '{candidate.FileName}' succeeded on {HookScriptHelper.GetEventKeyword(triggerEvent)}{FormatDetailSuffix(detail)}.");

            return new HookRunResult(candidate.FileName, outcome.Run.Id, outcome.Run.Status, Skipped: false, Error: null);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private async Task<Run> InsertSkippedRunAsync(
        string repositoryRoot,
        HookPipelineCandidate candidate,
        string triggerKey,
        string? detail,
        CancellationToken ct)
    {
        var repositoryId = await GetOrCreateRepositoryIdAsync(repositoryRoot, ct);
        var now = DateTimeOffset.UtcNow;

        var run = new Run
        {
            RepositoryId = repositoryId,
            PipelineFileName = candidate.FileName,
            PipelineName = candidate.Pipeline?.Name ?? candidate.FileName,
            StartedAt = now,
            FinishedAt = now,
            // RunStatus.Succeeded is the default (0); Skipped must always be set explicitly.
            Status = RunStatus.Skipped,
            ExitCode = null,
            Trigger = triggerKey,
            TriggerDetail = detail,
        };

        _dbContext.Runs.Add(run);
        await _dbContext.SaveChangesAsync(ct);

        return run;
    }

    private async Task<int> GetOrCreateRepositoryIdAsync(string repositoryPath, CancellationToken ct)
    {
        var normalizedPath = Path.GetFullPath(repositoryPath);

        var existing = await _dbContext.Repositories
            .FirstOrDefaultAsync(repository => repository.Path == normalizedPath, ct);

        if (existing is not null)
        {
            return existing.Id;
        }

        var repository = new Repository { Path = normalizedPath };
        _dbContext.Repositories.Add(repository);

        try
        {
            await _dbContext.SaveChangesAsync(ct);
            return repository.Id;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Another thread inserted the row first: detach the failed entity so the next
            // SaveChangesAsync doesn't retry the duplicate insert, then read the winner's id.
            _dbContext.Entry(repository).State = EntityState.Detached;
            var retry = await _dbContext.Repositories
                .FirstOrDefaultAsync(repo => repo.Path == normalizedPath, ct);
            if (retry is not null)
            {
                return retry.Id;
            }

            throw;
        }
    }

    /// <summary>
    /// True when the update failed because of a UNIQUE constraint (SQLite error 19), i.e. a
    /// concurrent writer inserted the same row first. The SQLite exception is matched by message so
    /// the Engine does not take a direct dependency on <c>Microsoft.Data.Sqlite</c>.
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task NotifyAsync(bool notify, NotificationLevel level, bool failed, string title, string message)
    {
        // Suppression matrix (pipeline preference + --no-notify) lives in NotificationPolicy; the
        // RAILWAY_HOOKS_NOTIFY=0 global kill switch is enforced inside NotificationService.
        if (_notificationService is null || !NotificationPolicy.ShouldNotify(notify, level, failed))
        {
            return;
        }

        try
        {
            await _notificationService.NotifyAsync(title, message, level);
        }
        catch (Exception)
        {
            // A failed notification must never fail a run.
        }
    }

    private static string FormatDetailSuffix(string? detail) =>
        string.IsNullOrEmpty(detail) ? string.Empty : $" ({detail})";
}

/// <summary>
/// Selects the pipelines subscribed to a hook event, preserving discovery order and applying the
/// optional single-pipeline filter and push branch/tag filters.
/// </summary>
public static class HookPipelineSelector
{
    /// <summary>
    /// Selects the candidates whose triggers match <paramref name="triggerEvent"/>.
    /// </summary>
    /// <param name="candidates">Candidates in discovery order.</param>
    /// <param name="triggerEvent">The fired event.</param>
    /// <param name="context">Event context supplying branch/tag values for push filtering.</param>
    /// <param name="pipelineFile">Optional pipeline file name filter (matched on the file name, like <c>PipelineRunner</c>).</param>
    public static IReadOnlyList<HookPipelineCandidate> Select(
        IReadOnlyList<HookPipelineCandidate> candidates,
        TriggerEvent triggerEvent,
        HookEventContext context,
        string? pipelineFile)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);

        var filtered = candidates;

        if (pipelineFile is not null)
        {
            filtered = [.. candidates.Where(candidate => Path.GetFileName(candidate.FileName) == pipelineFile)];
        }

        return [.. filtered.Where(candidate =>
            candidate.Pipeline is null
                ? pipelineFile is not null
                : MatchesEvent(candidate.Pipeline, triggerEvent, context))];
    }

    /// <summary>True when the pipeline subscribes to the event and its filters match the context.</summary>
    /// <param name="pipeline">The parsed pipeline.</param>
    /// <param name="triggerEvent">The fired event.</param>
    /// <param name="context">Event context.</param>
    public static bool MatchesEvent(Pipeline pipeline, TriggerEvent triggerEvent, HookEventContext context)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return pipeline.Triggers.Any(trigger => MatchesTrigger(trigger, triggerEvent, context));
    }

    /// <summary>True when a single trigger entry matches the event and filters.</summary>
    /// <param name="trigger">The trigger entry.</param>
    /// <param name="triggerEvent">The fired event.</param>
    /// <param name="context">Event context.</param>
    public static bool MatchesTrigger(TriggerDefinition trigger, TriggerEvent triggerEvent, HookEventContext context)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        if (trigger.Event != triggerEvent)
        {
            return false;
        }

        if (triggerEvent == TriggerEvent.PostCheckout
            && IsCloneCheckout(context)
            && !trigger.IncludeClone)
        {
            // git's post-checkout clone (previous HEAD all zeros) is excluded unless the pipeline opts
            // in with `post-checkout: { clone: true }`.
            return false;
        }

        if (triggerEvent != TriggerEvent.PrePush)
        {
            return true;
        }

        // A push carries either a branch or a tag, so configured filters are OR-combined
        // (matching either kind triggers the pipeline); no filters at all matches every push.
        var branchFiltered = trigger.Branches.Count > 0;
        var tagFiltered = trigger.Tags.Count > 0;

        if (!branchFiltered && !tagFiltered)
        {
            return true;
        }

        return (branchFiltered && MatchesBranchFilter(trigger.Branches, context.Branch))
            || (tagFiltered && MatchesTagFilter(trigger.Tags, context.Tag));
    }

    /// <summary>
    /// True when <paramref name="pattern"/> (with <c>*</c>/<c>?</c> wildcards) matches
    /// <paramref name="value"/> (case-sensitive).
    /// </summary>
    /// <param name="pattern">Wildcard pattern.</param>
    /// <param name="value">Value to test.</param>
    public static bool MatchesWildcard(string pattern, string value)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(value);

        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// True when a checkout context describes git's initial clone checkout: git passes the null object
    /// name (all zeros) as the previous HEAD.
    /// </summary>
    /// <param name="context">Checkout event context.</param>
    private static bool IsCloneCheckout(HookEventContext context) =>
        context.PreviousSha is { Length: > 0 } previous && previous.IndexOfAnyExcept('0') < 0;

    private static bool MatchesBranchFilter(IReadOnlyList<string> branches, string? branch)
    {
        if (branches.Count == 0)
        {
            return true;
        }

        return branch is not null && branches.Any(filter => MatchesWildcard(filter, branch));
    }

    private static bool MatchesTagFilter(IReadOnlyList<string> tags, string? tag)
    {
        if (tags.Count == 0)
        {
            return true;
        }

        return tag is not null && tags.Any(filter => MatchesWildcard(filter, tag));
    }
}

/// <summary>
/// Default <see cref="IHookPipelineSource"/>: discovers pipeline files and parses each one, preserving
/// discovery order and surfacing parse errors per candidate.
/// </summary>
public sealed class PipelineHookSource : IHookPipelineSource
{
    private readonly IPipelineDiscoveryService _pipelineDiscoveryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineHookSource"/> class.
    /// </summary>
    /// <param name="pipelineDiscoveryService">The pipeline discovery service.</param>
    public PipelineHookSource(IPipelineDiscoveryService pipelineDiscoveryService)
    {
        _pipelineDiscoveryService = pipelineDiscoveryService;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HookPipelineCandidate>> GetPipelinesAsync(string railwayPath)
    {
        var discovered = await _pipelineDiscoveryService.Discover(railwayPath);
        var candidates = new List<HookPipelineCandidate>(discovered.Count);

        foreach (var pipeline in discovered)
        {
            if (!pipeline.IsValid)
            {
                candidates.Add(new HookPipelineCandidate(pipeline.FileName, pipeline.Path, null, pipeline.Errors));
                continue;
            }

            var (parsed, error) = await PipelineHelper.TryParse(pipeline.Path);

            candidates.Add(error is null && parsed is not null
                ? new HookPipelineCandidate(pipeline.FileName, pipeline.Path, parsed, [])
                : new HookPipelineCandidate(
                    pipeline.FileName,
                    pipeline.Path,
                    null,
                    error is null ? [] : [error]));
        }

        return candidates;
    }
}

/// <summary>
/// File-backed <see cref="IProgress{T}"/> sink that appends run events to a per-run log under
/// <c>.railway/logs/</c>. <see cref="Report"/> is synchronous on the calling thread so lines keep
/// execution order.
/// </summary>
internal sealed class FileRunEventProgress : IProgress<RunEvent>, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public FileRunEventProgress(
        string logPath,
        string pipelineFileName,
        TriggerEvent triggerEvent,
        string triggerKey,
        string? detail)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        _writer = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };

        _writer.WriteLine($"# railway hook log");
        _writer.WriteLine($"pipeline: {pipelineFileName}");
        _writer.WriteLine($"event: {HookScriptHelper.GetEventKeyword(triggerEvent)} (trigger={triggerKey})");
        _writer.WriteLine($"detail: {detail ?? string.Empty}");
        _writer.WriteLine($"started: {DateTimeOffset.UtcNow:O}");
        _writer.WriteLine();
    }

    /// <inheritdoc />
    public void Report(RunEvent @event)
    {
        lock (_gate)
        {
            var builder = new StringBuilder();
            builder.Append('[').Append(@event.Timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")).Append("] ");
            builder.Append(@event.Kind);

            if (!string.IsNullOrEmpty(@event.JobName))
            {
                builder.Append(" job=").Append(@event.JobName);
            }

            if (!string.IsNullOrEmpty(@event.StepName))
            {
                builder.Append(" step=").Append(@event.StepName);
            }

            if (@event.ExitCode is not null)
            {
                builder.Append(" exit=").Append(@event.ExitCode);
            }

            if (!string.IsNullOrEmpty(@event.Text))
            {
                builder.Append(' ').Append(@event.Text);
            }

            _writer.WriteLine(builder.ToString());
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}
