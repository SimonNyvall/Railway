using System.Text;

using Microsoft.EntityFrameworkCore;

using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Engine.Services.Interfaces;
using Railway.Engine.Services.Schedulers;

namespace Railway.Engine.Orchestration;

/// <summary>
/// Installs, removes, inspects and re-syncs the git-hook half of a repository's pipeline triggers.
/// </summary>
/// <remarks>
/// Artifacts: a POSIX <c>sh</c> wrapper per git event under <c>.railway/hooks/</c>, a git hook file per
/// event in the git hooks directory (resolved through <c>git rev-parse --git-path hooks</c>, so worktrees
/// and <c>core.hooksPath</c> are honored), and one marker-delimited <c>.gitignore</c> block. A foreign
/// hook found at a target path is moved to <c>&lt;hook&gt;.railway-backup</c> and chain-called by our hook.
/// <c>schedule</c> triggers are additionally materialized through <see cref="ISchedulerSelector"/> (one
/// entry per (pipeline, cron), using a generated per-pipeline wrapper where the backend needs one).
/// </remarks>
public sealed class HookInstaller : IHookInstaller
{
    private static readonly string[] ManagedGitHookNames = ["post-commit", "pre-push", "post-merge", "post-checkout"];

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly HookPaths _paths;
    private readonly ISystemCommandRunner _systemCommandRunner;
    private readonly IHookLauncher _launcher;
    private readonly IHookPipelineSource _pipelineSource;
    private readonly RailwayDbContext? _dbContext;
    private readonly ISchedulerSelector? _schedulerSelector;

    /// <summary>
    /// Initializes a new instance of the <see cref="HookInstaller"/> class.
    /// </summary>
    /// <param name="paths">Resolves wrapper, log and lock paths.</param>
    /// <param name="systemCommandRunner">Runs <c>git rev-parse</c> to resolve the hooks directory.</param>
    /// <param name="launcher">Supplies the launcher embedded in generated wrappers.</param>
    /// <param name="pipelineSource">Loads the repository's pipelines to compute referenced events.</param>
    /// <param name="dbContext">Optional database used to surface the last run in status; null disables it.</param>
    /// <param name="schedulerSelector">
    /// Selects the OS scheduler used for <c>schedule</c> triggers; null disables schedule materialization.
    /// </param>
    public HookInstaller(
        HookPaths paths,
        ISystemCommandRunner systemCommandRunner,
        IHookLauncher launcher,
        IHookPipelineSource pipelineSource,
        RailwayDbContext? dbContext = null,
        ISchedulerSelector? schedulerSelector = null)
    {
        _paths = paths;
        _systemCommandRunner = systemCommandRunner;
        _launcher = launcher;
        _pipelineSource = pipelineSource;
        _dbContext = dbContext;
        _schedulerSelector = schedulerSelector;
    }

    /// <inheritdoc />
    public async Task<InstallResult> Install(string repositoryPath)
    {
        var railwayPath = RepositoryHelper.GetRailwayPathOrThrow(repositoryPath);
        var repositoryRoot = GetRepositoryRootOrThrow(repositoryPath);

        var candidates = await _pipelineSource.GetPipelinesAsync(railwayPath);
        var gitEvents = GetGitHookEvents(candidates);
        var scheduleRequests = GetScheduleRequests(candidates);

        var artifacts = new List<HookArtifact>();
        var launcher = _launcher.GetLaunchCommand();

        EnsureGitArtifacts(repositoryRoot, gitEvents, launcher, artifacts);

        var scheduleErrors = new List<string>();
        var scheduleWarnings = new List<string>();

        MaterializeSchedules(repositoryRoot, scheduleRequests, launcher, artifacts, scheduleErrors, scheduleWarnings);

        var gitignoreChanged = HasAnyTrigger(candidates) && EnsureGitignoreBlock(repositoryRoot, out _);

        return new InstallResult(repositoryRoot, artifacts, gitignoreChanged, scheduleErrors, scheduleWarnings);
    }

    /// <inheritdoc />
    public async Task<UninstallResult> Uninstall(string repositoryPath, bool purge = false)
    {
        var repositoryRoot = GetRepositoryRootOrThrow(repositoryPath);
        var artifacts = new List<HookArtifact>();
        var warnings = new List<string>();

        var hooksDirectory = ResolveGitHooksDirectory(repositoryRoot);

        foreach (var hookName in ManagedGitHookNames)
        {
            var hookPath = Path.Combine(hooksDirectory, hookName);

            if (!File.Exists(hookPath))
            {
                continue;
            }

            var content = File.ReadAllText(hookPath);

            if (!HookScriptHelper.IsRailwayManagedScript(content))
            {
                warnings.Add($"Hook '{hookName}' is not managed by railway and was left in place.");
                continue;
            }

            File.Delete(hookPath);

            var backupPath = hookPath + HookScriptHelper.BackupSuffix;

            if (File.Exists(backupPath))
            {
                File.Move(backupPath, hookPath, overwrite: true);
                artifacts.Add(new HookArtifact(HookArtifactKind.GitHook, hookName, hookPath, HookArtifactAction.Restored));
            }
            else
            {
                artifacts.Add(new HookArtifact(HookArtifactKind.GitHook, hookName, hookPath, HookArtifactAction.Removed));
            }
        }

        RemoveWrapperArtifacts(repositoryRoot, artifacts);

        RemoveGitignoreBlock(repositoryRoot, artifacts);

        RemoveSchedules(repositoryRoot, artifacts, warnings);

        var purgedLogs = false;
        var logsDirectory = _paths.GetLogsDirectory(repositoryRoot);

        if (purge && Directory.Exists(logsDirectory))
        {
            Directory.Delete(logsDirectory, recursive: true);
            purgedLogs = true;
            artifacts.Add(new HookArtifact(HookArtifactKind.Logs, HookPaths.LogsFolderName, logsDirectory, HookArtifactAction.Removed));
        }

        return new UninstallResult(repositoryRoot, artifacts, purgedLogs, warnings);
    }

    /// <inheritdoc />
    public async Task<HookStatusReport> Status(string repositoryPath)
    {
        var railwayPath = RepositoryHelper.GetRailwayPathOrThrow(repositoryPath);
        var repositoryRoot = GetRepositoryRootOrThrow(repositoryPath);

        var candidates = await _pipelineSource.GetPipelinesAsync(railwayPath);
        var hooksDirectory = ResolveGitHooksDirectory(repositoryRoot);
        var scheduler = GetScheduleRequests(candidates).Count > 0 ? _schedulerSelector?.Select() : null;

        var pipelines = new List<PipelineHookStatus>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var triggers = new List<TriggerStatus>();

            foreach (var trigger in candidate.Pipeline?.Triggers ?? [])
            {
                triggers.Add(BuildTriggerStatus(repositoryRoot, hooksDirectory, candidate.FileName, trigger, scheduler));
            }

            pipelines.Add(new PipelineHookStatus(
                candidate.FileName,
                triggers,
                candidate.Errors,
                LockHeld: File.Exists(_paths.GetLockFilePath(repositoryRoot, candidate.FileName)),
                LastRun: await GetLastRunAsync(repositoryRoot, candidate.FileName)));
        }

        return new HookStatusReport(
            repositoryRoot,
            hooksDirectory,
            GitignoreConfigured(repositoryRoot),
            pipelines,
            scheduler?.Name);
    }

    /// <inheritdoc />
    public async Task<SyncResult> Sync(string repositoryPath)
    {
        var railwayPath = RepositoryHelper.GetRailwayPathOrThrow(repositoryPath);
        var repositoryRoot = GetRepositoryRootOrThrow(repositoryPath);

        var candidates = await _pipelineSource.GetPipelinesAsync(railwayPath);
        var gitEvents = GetGitHookEvents(candidates);
        var scheduleRequests = GetScheduleRequests(candidates);
        var launcher = _launcher.GetLaunchCommand();
        var scheduler = scheduleRequests.Count > 0 ? _schedulerSelector?.Select() : null;

        var scheduleWrapperNames = scheduler?.UsesWrapperScript == true
            ? scheduleRequests.Select(request => HookScriptHelper.GetScheduleWrapperScriptName(request.PipelineFileName)).ToList()
            : (IReadOnlyList<string>)[ ];

        var removed = RemoveStaleGitArtifacts(repositoryRoot, gitEvents, scheduleWrapperNames);

        var artifacts = new List<HookArtifact>();
        EnsureGitArtifacts(repositoryRoot, gitEvents, launcher, artifacts);

        var added = new List<string>();
        var updated = new List<string>();
        var errors = new List<string>();

        foreach (var artifact in artifacts)
        {
            ApplyDiff(artifact, added, updated);
        }

        if (HasAnyTrigger(candidates) && EnsureGitignoreBlock(repositoryRoot, out var gitignoreChanged) && gitignoreChanged)
        {
            updated.Add(".gitignore block");
        }

        if (scheduler is not null)
        {
            // Keep the scheduler redirect target present (see MaterializeSchedules).
            Directory.CreateDirectory(_paths.GetLogsDirectory(repositoryRoot));

            var wrappers = new List<HookArtifact>();
            EnsureScheduleWrappers(repositoryRoot, scheduleRequests, launcher, scheduler, wrappers);

            foreach (var wrapper in wrappers)
            {
                ApplyDiff(wrapper, added, updated);
            }

            foreach (var outcome in scheduler.Reconcile(repositoryRoot, scheduleRequests).Entries)
            {
                switch (outcome.Action)
                {
                    case ScheduleAction.Created:
                        added.Add(DescribeSchedule(outcome.PipelineFileName, outcome.Cron));
                        break;
                    case ScheduleAction.Updated:
                        updated.Add(DescribeSchedule(outcome.PipelineFileName, outcome.Cron));
                        break;
                    case ScheduleAction.Removed:
                        removed.Add(DescribeSchedule(outcome.PipelineFileName, outcome.Cron));
                        break;
                    case ScheduleAction.Failed:
                        errors.Add(outcome.Error ?? $"schedule {outcome.PipelineFileName} ({outcome.Cron}) failed");
                        break;
                }
            }
        }

        return new SyncResult(repositoryRoot, added, removed, updated, errors);
    }

    private static void ApplyDiff(HookArtifact artifact, List<string> added, List<string> updated)
    {
        switch (artifact.Action)
        {
            case HookArtifactAction.Created:
            case HookArtifactAction.Chained:
                added.Add(DescribeArtifact(artifact));
                break;
            case HookArtifactAction.Updated:
                updated.Add(DescribeArtifact(artifact));
                break;
        }
    }

    private static string DescribeSchedule(string pipelineFileName, string cron) =>
        $"schedule {pipelineFileName} ({cron})";

    private static string DescribeArtifact(HookArtifact artifact) => artifact.Kind switch
    {
        HookArtifactKind.GitHook => $"git hook {artifact.Name}",
        HookArtifactKind.Wrapper => $"wrapper {artifact.Name}",
        HookArtifactKind.Schedule => $"schedule {artifact.Name}",
        _ => artifact.Name,
    };

    private static bool HasAnyTrigger(IReadOnlyList<HookPipelineCandidate> candidates) =>
        candidates.Any(candidate => candidate.Pipeline?.Triggers.Count > 0);

    private static List<TriggerEvent> GetGitHookEvents(IReadOnlyList<HookPipelineCandidate> candidates) =>
        [.. candidates
            .SelectMany(candidate => candidate.Pipeline?.Triggers ?? [])
            .Select(trigger => trigger.Event)
            .Where(HookScriptHelper.IsGitHookEvent)
            .Distinct()
            .OrderBy(triggerEvent => (int)triggerEvent)];

    private static List<ScheduleRequest> GetScheduleRequests(IReadOnlyList<HookPipelineCandidate> candidates) =>
        [.. candidates
            .SelectMany(candidate => (candidate.Pipeline?.Triggers ?? [])
                .Where(trigger => trigger.Event == TriggerEvent.Schedule
                    && trigger.Errors.Count == 0
                    && !string.IsNullOrWhiteSpace(trigger.Cron))
                .Select(trigger => new ScheduleRequest(candidate.FileName, trigger.Cron!)))
            .Distinct()];

    private void MaterializeSchedules(
        string repositoryRoot,
        IReadOnlyList<ScheduleRequest> requests,
        HookLaunchCommand launcher,
        List<HookArtifact> artifacts,
        List<string> errors,
        List<string> warnings)
    {
        if (requests.Count == 0)
        {
            return;
        }

        if (_schedulerSelector is null)
        {
            return;
        }

        var scheduler = _schedulerSelector.Select();

        // Scheduler entries redirect their output into .railway/logs; ensure it exists at install time
        // (the runner would only create it after a run, which a missing redirect target prevents).
        Directory.CreateDirectory(_paths.GetLogsDirectory(repositoryRoot));

        EnsureScheduleWrappers(repositoryRoot, requests, launcher, scheduler, artifacts);

        foreach (var request in requests)
        {
            ApplyScheduleInstallOutcome(scheduler.Install(repositoryRoot, request), artifacts, errors, warnings);
        }
    }

    private void EnsureScheduleWrappers(
        string repositoryRoot,
        IReadOnlyList<ScheduleRequest> requests,
        HookLaunchCommand launcher,
        IScheduler scheduler,
        List<HookArtifact> artifacts)
    {
        if (!scheduler.UsesWrapperScript)
        {
            return;
        }

        foreach (var pipelineFileName in requests
            .Select(request => request.PipelineFileName)
            .Distinct(StringComparer.Ordinal))
        {
            var wrapperPath = SchedulerBase.ResolveScheduleWrapperScriptPath(_paths, repositoryRoot, pipelineFileName);
            var content = HookScriptHelper.BuildScheduleWrapperScript(
                pipelineFileName,
                repositoryRoot,
                launcher,
                HookScriptHelper.BuildScheduleSafePath(launcher));

            artifacts.Add(new HookArtifact(
                HookArtifactKind.Wrapper,
                Path.GetFileName(wrapperPath),
                wrapperPath,
                WriteScript(wrapperPath, content)));
        }
    }

    private static void ApplyScheduleInstallOutcome(
        ScheduleInstallOutcome outcome,
        List<HookArtifact> artifacts,
        List<string> errors,
        List<string> warnings)
    {
        if (outcome.Action == ScheduleAction.Failed)
        {
            errors.Add(outcome.Error ?? $"Schedule '{outcome.Cron}' for pipeline '{outcome.PipelineFileName}' could not be materialized.");
            if (outcome.Warning is not null)
            {
                warnings.Add(outcome.Warning);
            }
            return;
        }

        if (outcome.Warning is not null)
        {
            warnings.Add(outcome.Warning);
        }

        if (outcome.Action == ScheduleAction.Unchanged)
        {
            return;
        }

        artifacts.Add(new HookArtifact(
            HookArtifactKind.Schedule,
            DescribeSchedule(outcome.PipelineFileName, outcome.Cron),
            outcome.EntryName,
            outcome.Action == ScheduleAction.Created ? HookArtifactAction.Created : HookArtifactAction.Updated));
    }

    private void RemoveSchedules(string repositoryRoot, List<HookArtifact> artifacts, List<string> warnings)
    {
        if (_schedulerSelector is null)
        {
            return;
        }

        foreach (var outcome in _schedulerSelector.Select().Reconcile(repositoryRoot, []).Entries)
        {
            switch (outcome.Action)
            {
                case ScheduleAction.Removed:
                    artifacts.Add(new HookArtifact(
                        HookArtifactKind.Schedule,
                        DescribeSchedule(outcome.PipelineFileName, outcome.Cron),
                        outcome.EntryName,
                        HookArtifactAction.Removed));
                    break;
                case ScheduleAction.Failed:
                    warnings.Add(outcome.Error ?? $"Schedule '{outcome.Cron}' for pipeline '{outcome.PipelineFileName}' could not be removed.");
                    break;
            }
        }
    }

    private void EnsureGitArtifacts(
        string repositoryRoot,
        IReadOnlyList<TriggerEvent> gitEvents,
        HookLaunchCommand launcher,
        List<HookArtifact> artifacts)
    {
        if (gitEvents.Count == 0)
        {
            return;
        }

        var hooksDirectory = ResolveGitHooksDirectory(repositoryRoot);
        Directory.CreateDirectory(hooksDirectory);

        foreach (var triggerEvent in gitEvents)
        {
            var wrapperPath = _paths.GetWrapperScriptPath(repositoryRoot, triggerEvent);
            var wrapperAction = WriteScript(
                wrapperPath,
                HookScriptHelper.BuildWrapperScript(triggerEvent, repositoryRoot, launcher, detach: true));

            artifacts.Add(new HookArtifact(
                HookArtifactKind.Wrapper,
                Path.GetFileName(wrapperPath),
                wrapperPath,
                wrapperAction));
        }

        foreach (var triggerEvent in gitEvents)
        {
            var hookName = HookScriptHelper.GetGitHookName(triggerEvent)!;
            var hookPath = Path.Combine(hooksDirectory, hookName);
            var backupPath = hookPath + HookScriptHelper.BackupSuffix;
            var chained = false;

            if (File.Exists(hookPath) && !HookScriptHelper.IsRailwayManagedScript(File.ReadAllText(hookPath)))
            {
                // Preserve the user's hook: move it aside and chain-call it from ours.
                File.Move(hookPath, backupPath, overwrite: true);
                chained = true;
            }

            var hasBackup = File.Exists(backupPath);
            var wrapperPath = _paths.GetWrapperScriptPath(repositoryRoot, triggerEvent);
            var content = HookScriptHelper.BuildGitHookScript(
                triggerEvent,
                wrapperPath,
                hasBackup ? backupPath : null);

            var action = WriteScript(hookPath, content);

            artifacts.Add(new HookArtifact(
                HookArtifactKind.GitHook,
                hookName,
                hookPath,
                chained ? HookArtifactAction.Chained : action,
                chained ? backupPath : null));
        }
    }

    private List<string> RemoveStaleGitArtifacts(
        string repositoryRoot,
        IReadOnlyList<TriggerEvent> desiredEvents,
        IReadOnlyList<string>? extraDesiredWrappers = null)
    {
        var removed = new List<string>();

        var desiredHookNames = desiredEvents
            .Select(HookScriptHelper.GetGitHookName)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);

        var hooksDirectory = ResolveGitHooksDirectory(repositoryRoot);

        if (Directory.Exists(hooksDirectory))
        {
            foreach (var hookPath in Directory.EnumerateFiles(hooksDirectory))
            {
                var hookName = Path.GetFileName(hookPath);

                if (desiredHookNames.Contains(hookName) || !HookScriptHelper.IsRailwayManagedScript(File.ReadAllText(hookPath)))
                {
                    continue;
                }

                File.Delete(hookPath);

                var backupPath = hookPath + HookScriptHelper.BackupSuffix;

                if (File.Exists(backupPath))
                {
                    File.Move(backupPath, hookPath, overwrite: true);
                }

                removed.Add($"git hook {hookName}");
            }
        }

        var desiredWrapperNames = desiredEvents
            .Select(triggerEvent => Path.GetFileName(_paths.GetWrapperScriptPath(repositoryRoot, triggerEvent)))
            .ToHashSet(StringComparer.Ordinal);

        if (extraDesiredWrappers is not null)
        {
            desiredWrapperNames.UnionWith(extraDesiredWrappers);
        }

        var wrappersDirectory = _paths.GetHooksDirectory(repositoryRoot);

        if (Directory.Exists(wrappersDirectory))
        {
            foreach (var wrapperPath in Directory.EnumerateFiles(wrappersDirectory))
            {
                var wrapperName = Path.GetFileName(wrapperPath);

                if (desiredWrapperNames.Contains(wrapperName) || !HookScriptHelper.IsRailwayManagedScript(File.ReadAllText(wrapperPath)))
                {
                    continue;
                }

                File.Delete(wrapperPath);
                removed.Add($"wrapper {wrapperName}");
            }

            if (!Directory.EnumerateFileSystemEntries(wrappersDirectory).Any())
            {
                Directory.Delete(wrappersDirectory);
            }
        }

        return removed;
    }

    private void RemoveWrapperArtifacts(string repositoryRoot, List<HookArtifact> artifacts)
    {
        var wrappersDirectory = _paths.GetHooksDirectory(repositoryRoot);

        if (!Directory.Exists(wrappersDirectory))
        {
            return;
        }

        foreach (var wrapperPath in Directory.EnumerateFiles(wrappersDirectory))
        {
            if (!HookScriptHelper.IsRailwayManagedScript(File.ReadAllText(wrapperPath)))
            {
                continue;
            }

            File.Delete(wrapperPath);
            artifacts.Add(new HookArtifact(
                HookArtifactKind.Wrapper,
                Path.GetFileName(wrapperPath),
                wrapperPath,
                HookArtifactAction.Removed));
        }

        if (!Directory.EnumerateFileSystemEntries(wrappersDirectory).Any())
        {
            Directory.Delete(wrappersDirectory);
        }
    }

    private bool EnsureGitignoreBlock(string repositoryRoot, out bool changed)
    {
        var gitignorePath = Path.Combine(repositoryRoot, ".gitignore");
        var existing = File.Exists(gitignorePath) ? File.ReadAllText(gitignorePath) : null;
        var (content, didChange) = HookScriptHelper.UpsertGitignoreBlock(existing);

        changed = didChange;

        if (didChange)
        {
            File.WriteAllText(gitignorePath, content, Utf8NoBom);
        }

        return didChange;
    }

    private static void RemoveGitignoreBlock(string repositoryRoot, List<HookArtifact> artifacts)
    {
        var gitignorePath = Path.Combine(repositoryRoot, ".gitignore");

        if (!File.Exists(gitignorePath))
        {
            return;
        }

        var (content, removed) = HookScriptHelper.RemoveGitignoreBlock(File.ReadAllText(gitignorePath));

        if (!removed)
        {
            return;
        }

        if (content.Length == 0)
        {
            File.Delete(gitignorePath);
        }
        else
        {
            File.WriteAllText(gitignorePath, content, Utf8NoBom);
        }

        artifacts.Add(new HookArtifact(HookArtifactKind.Gitignore, ".gitignore", gitignorePath, HookArtifactAction.Removed));
    }

    private static bool GitignoreConfigured(string repositoryRoot)
    {
        var gitignorePath = Path.Combine(repositoryRoot, ".gitignore");

        return File.Exists(gitignorePath)
            && File.ReadAllText(gitignorePath).Contains(HookScriptHelper.GitignoreBlockStart, StringComparison.Ordinal);
    }

    private TriggerStatus BuildTriggerStatus(
        string repositoryRoot,
        string hooksDirectory,
        string pipelineFileName,
        TriggerDefinition trigger,
        IScheduler? scheduler)
    {
        var hookState = default(GitHookState?);
        string? hookPath = null;
        string? backupPath = null;

        var hookName = HookScriptHelper.GetGitHookName(trigger.Event);

        if (hookName is not null)
        {
            hookPath = Path.Combine(hooksDirectory, hookName);
            var candidateBackup = hookPath + HookScriptHelper.BackupSuffix;

            if (!File.Exists(hookPath))
            {
                hookState = GitHookState.Missing;
            }
            else
            {
                var content = File.ReadAllText(hookPath);

                if (!HookScriptHelper.IsRailwayManagedScript(content))
                {
                    hookState = GitHookState.Foreign;
                }
                else
                {
                    var hasBackup = File.Exists(candidateBackup);
                    backupPath = hasBackup ? candidateBackup : null;

                    var expected = HookScriptHelper.BuildGitHookScript(
                        trigger.Event,
                        _paths.GetWrapperScriptPath(repositoryRoot, trigger.Event),
                        backupPath);

                    hookState = content == expected
                        ? (hasBackup ? GitHookState.ForeignChained : GitHookState.Installed)
                        : GitHookState.Drifted;
                }
            }
        }

        var scheduleState = default(ScheduleState?);
        string? scheduleEntry = null;
        string? scheduleError = null;

        if (trigger.Event == TriggerEvent.Schedule
            && scheduler is not null
            && trigger.Errors.Count == 0
            && !string.IsNullOrWhiteSpace(trigger.Cron))
        {
            var status = scheduler.Inspect(repositoryRoot, new ScheduleRequest(pipelineFileName, trigger.Cron));
            scheduleState = status.State;
            scheduleEntry = status.EntryName;
            scheduleError = status.Error;
        }

        return new TriggerStatus(
            trigger.Event,
            trigger.Branches,
            trigger.Tags,
            trigger.WatchPaths,
            trigger.Cron,
            trigger.Errors,
            hookState,
            hookPath,
            backupPath,
            GetNextOccurrence(trigger),
            scheduleState,
            scheduleEntry,
            scheduleError);
    }

    private async Task<RunSummary?> GetLastRunAsync(string repositoryRoot, string pipelineFileName)
    {
        if (_dbContext is null)
        {
            return null;
        }

        var normalizedPath = Path.GetFullPath(repositoryRoot);

        var repositoryId = await _dbContext.Repositories
            .Where(repository => repository.Path == normalizedPath)
            .Select(repository => repository.Id)
            .FirstOrDefaultAsync();

        if (repositoryId == 0)
        {
            return null;
        }

        var run = await _dbContext.Runs
            .Where(candidate => candidate.RepositoryId == repositoryId && candidate.PipelineFileName == pipelineFileName)
            // SQLite cannot ORDER BY DateTimeOffset; run ids are monotonic with insertion, so the
            // highest id is the most recently created run for the pipeline.
            .OrderByDescending(candidate => candidate.Id)
            .FirstOrDefaultAsync();

        return run is null
            ? null
            : new RunSummary(run.Id, run.Status, run.StartedAt, run.Trigger, run.TriggerDetail);
    }

    private string ResolveGitHooksDirectory(string repositoryRoot)
    {
        var result = _systemCommandRunner.Run(
            "git",
            ["rev-parse", "--git-path", "hooks"],
            workingDirectory: repositoryRoot);

        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            var output = result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(output))
            {
                return Path.IsPathRooted(output)
                    ? output
                    : Path.GetFullPath(Path.Combine(repositoryRoot, output));
            }
        }

        // Not a git repository (or git unavailable): fall back to the conventional location.
        return Path.Combine(repositoryRoot, ".git", "hooks");
    }

    /// <summary>
    /// Next occurrence (local time) of a valid schedule trigger, or null for other events or an
    /// unparseable expression.
    /// </summary>
    /// <param name="trigger">The trigger to inspect.</param>
    private static DateTimeOffset? GetNextOccurrence(TriggerDefinition trigger)
    {
        if (trigger.Event != TriggerEvent.Schedule || string.IsNullOrWhiteSpace(trigger.Cron) || trigger.Errors.Count > 0)
        {
            return null;
        }

        try
        {
            var next = CronExpressionHelper.Parse(trigger.Cron).GetNextOccurrence(DateTime.Now, TimeZoneInfo.Local);
            return new DateTimeOffset(next, TimeZoneInfo.Local.GetUtcOffset(next));
        }
        catch (CronExpressionException)
        {
            return null;
        }
    }

    private static string GetRepositoryRootOrThrow(string repositoryPath) =>
        RepositoryHelper.GetRootRepositoryPath(repositoryPath) ?? throw new NotAGitRepositoryException();

    private static HookArtifactAction WriteScript(string path, string content)
    {
        if (!File.Exists(path))
        {
            WriteAllText(path, content);
            MakeExecutable(path);
            return HookArtifactAction.Created;
        }

        if (string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
        {
            MakeExecutable(path);
            return HookArtifactAction.Unchanged;
        }

        WriteAllText(path, content);
        MakeExecutable(path);
        return HookArtifactAction.Updated;
    }

    private static void WriteAllText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8NoBom);
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(
                path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Non-POSIX file system; git will still invoke the script through sh.
        }
    }
}
