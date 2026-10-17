using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Railway.Engine.Orchestration.Interfaces;

namespace Railway.Engine.Orchestration;

/// <summary>
/// Default <see cref="IHookWatcher"/>: watches a repository's working tree recursively, matches each
/// changed file against the pipelines' <c>watch</c> globs, coalesces bursts with a per-pipeline
/// trailing debounce and runs the matches through <see cref="IHookRunner"/> with
/// <see cref="TriggerEvent.Watch"/>. Never writes to stdout.
/// </summary>
/// <remarks>
/// <para>
/// Repository resolution is explicit (<see cref="RepositoryHelper"/>, never the current directory).
/// Ignored before matching (always): any path with a <c>.git</c>/<c>.railway</c>/<c>bin</c>/<c>obj</c>/<c>node_modules</c>
/// segment, directory events, and git-ignored paths (checked with <c>git check-ignore</c> through
/// <see cref="ISystemCommandRunner"/>; a missing/failing git degrades to "not ignored").
/// </para>
/// <para>
/// The file-event ingestion is an injectable seam (<see cref="IWatchEventSource"/>); <see cref="OnPathChanged"/>
/// is also public so tests can drive events synchronously without the real watcher.
/// </para>
/// </remarks>
public sealed class HookWatcher : IHookWatcher
{
    /// <summary>Directory/file name segments whose contents are never watched, at any depth.</summary>
    private static readonly HashSet<string> IgnoredSegments = new(StringComparer.Ordinal)
    {
        ".git",
        ".railway",
        "bin",
        "obj",
        "node_modules",
    };

    private readonly IHookPipelineSource _pipelineSource;
    private readonly IHookRunner _hookRunner;
    private readonly ISystemCommandRunner _systemCommandRunner;
    private readonly IWatchEventSource _eventSource;
    private readonly Action<string> _warn;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Dictionary<string, PendingRun> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedGlobs = new(StringComparer.Ordinal);

    private List<WatchPipeline> _watchPipelines = [];
    private string? _root;
    private TimeSpan _debounce = WatchDefaults.Debounce;
    private bool _notify = true;
    private Action<WatchPathChange>? _onChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="HookWatcher"/> class.
    /// </summary>
    /// <param name="pipelineSource">Loads the repository's pipelines for watch-trigger discovery.</param>
    /// <param name="hookRunner">Runs the selected pipelines with watch provenance.</param>
    /// <param name="systemCommandRunner">Runs <c>git check-ignore</c> for the gitignore filter.</param>
    /// <param name="eventSource">File-system event seam; the production source wraps <see cref="FileSystemWatcher"/>.</param>
    /// <param name="warn">
    /// Optional sink for degradation warnings (e.g. an unusable watch glob); defaults to standard
    /// error so a misconfigured glob never fails silently. Warnings are never written to stdout.
    /// </param>
    public HookWatcher(
        IHookPipelineSource pipelineSource,
        IHookRunner hookRunner,
        ISystemCommandRunner systemCommandRunner,
        IWatchEventSource eventSource,
        Action<string>? warn = null)
    {
        _pipelineSource = pipelineSource;
        _hookRunner = hookRunner;
        _systemCommandRunner = systemCommandRunner;
        _eventSource = eventSource;
        _warn = warn ?? (static message => Console.Error.WriteLine(message));
    }

    /// <summary>Gets a value that indicates whether the file watcher is currently armed.</summary>
    /// <remarks>Exposed so hosts (and tests) can wait until the watcher is actually armed before mutating files.</remarks>
    public bool IsWatching { get; private set; }

    /// <inheritdoc />
    public async Task WatchAsync(
        string repositoryPath,
        HookWatchOptions options,
        IProgress<HookRunResult>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var railwayPath = RepositoryHelper.GetRailwayPathOrThrow(repositoryPath);
        var root = RepositoryHelper.GetRootRepositoryPath(repositoryPath)
            ?? Path.GetDirectoryName(railwayPath)
            ?? repositoryPath;

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _debounce = options.Debounce ?? WatchDefaults.Debounce;

        if (_debounce < TimeSpan.Zero)
        {
            _debounce = TimeSpan.Zero;
        }

        _notify = options.Notify;
        _watchPipelines = await DiscoverWatchPipelinesAsync(railwayPath, options.PipelineFile);

        _onChanged = change => OnPathChanged(change.FullPath, change.IsDirectory);
        _eventSource.Changed += _onChanged;

        try
        {
            if (options.Initial)
            {
                await RunInitialAsync(progress, ct);
            }

            ct.ThrowIfCancellationRequested();

            _eventSource.Start(_root);
            IsWatching = true;

            await LoopAsync(progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ctrl+C / host shutdown: a clean stop, not an error.
        }
        finally
        {
            IsWatching = false;
            _eventSource.Stop();

            if (_onChanged is not null)
            {
                _eventSource.Changed -= _onChanged;
                _onChanged = null;
            }
        }
    }

    /// <summary>
    /// Ingests one file-system change. Called by the active <see cref="IWatchEventSource"/>; also
    /// public so tests can drive events synchronously without a real watcher.
    /// </summary>
    /// <param name="fullPath">Absolute path of the changed entry.</param>
    /// <param name="isDirectory">True when the changed entry is a directory (ignored).</param>
    public void OnPathChanged(string fullPath, bool isDirectory = false)
    {
        if (_root is null || _watchPipelines.Count == 0 || string.IsNullOrEmpty(fullPath) || isDirectory)
        {
            return;
        }

        if (!TryGetRelativePosixPath(_root, fullPath, out var relativePath))
        {
            return;
        }

        if (IsInIgnoredDirectory(relativePath) || IsGitIgnored(relativePath))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var matched = false;

        lock (_gate)
        {
            foreach (var pipeline in _watchPipelines)
            {
                if (!MatchesAnyGlobWithWarnings(pipeline.Globs, relativePath))
                {
                    continue;
                }

                _pending[pipeline.FileName] = new PendingRun(pipeline.FileName, relativePath, now);
                matched = true;
            }
        }

        if (matched)
        {
            Signal();
        }
    }

    /// <summary>
    /// Determines whether <paramref name="pattern"/> (supporting <c>**</c>, <c>*</c>, <c>?</c> and
    /// character classes) matches the repo-relative, POSIX-separated <paramref name="path"/>
    /// (case-sensitive).
    /// </summary>
    /// <param name="pattern">Watch glob.</param>
    /// <param name="path">Repo-relative path using <c>/</c> separators.</param>
    /// <returns><see langword="true" /> when <paramref name="pattern"/> matches <paramref name="path"/>; otherwise, <see langword="false" />.</returns>
    public static bool MatchesGlob(string pattern, string path)
    {
        return HookWatchGlobMatcher.MatchesGlob(pattern, path);
    }

    /// <summary>Determines whether any glob in <paramref name="globs"/> matches <paramref name="path"/>.</summary>
    /// <param name="globs">Watch globs.</param>
    /// <param name="path">Repo-relative path using <c>/</c> separators.</param>
    /// <returns><see langword="true" /> when any glob in <paramref name="globs"/> matches <paramref name="path"/>; otherwise, <see langword="false" />.</returns>
    public static bool MatchesAnyGlob(IEnumerable<string> globs, string path)
    {
        return HookWatchGlobMatcher.MatchesAnyGlob(globs, path);
    }

    /// <summary>Matches one path against a set of globs, diagnosing unusable globs exactly once.</summary>
    private bool MatchesAnyGlobWithWarnings(IEnumerable<string> globs, string path)
    {
        foreach (var glob in globs)
        {
            switch (HookWatchGlobMatcher.EvaluateGlob(glob, path))
            {
                case HookWatchGlobMatcher.GlobOutcome.Match:
                    return true;

                case HookWatchGlobMatcher.GlobOutcome.Malformed:
                    WarnOnce(glob, $"watch glob '{glob}' is not a usable pattern and will be ignored.");
                    break;

                case HookWatchGlobMatcher.GlobOutcome.TimedOut:
                    WarnOnce(glob, $"watch glob '{glob}' took too long to match and was skipped.");
                    break;
            }
        }

        return false;
    }

    /// <summary>Emits a degradation warning for <paramref name="glob"/> at most once per session.</summary>
    private void WarnOnce(string glob, string message)
    {
        lock (_warnedGlobs)
        {
            if (!_warnedGlobs.Add(glob))
            {
                return;
            }
        }

        _warn(message);
    }

    /// <summary>
    /// Determines whether any segment of <paramref name="relativePosixPath"/> is one of the
    /// always-ignored directories (<c>.git</c>, <c>.railway</c>, <c>bin</c>, <c>obj</c>, <c>node_modules</c>).
    /// </summary>
    /// <param name="relativePosixPath">Repo-relative path using <c>/</c> separators.</param>
    /// <returns><see langword="true" /> when any segment of <paramref name="relativePosixPath"/> is one of the always-ignored directories; otherwise, <see langword="false" />.</returns>
    public static bool IsInIgnoredDirectory(string relativePosixPath)
    {
        ArgumentNullException.ThrowIfNull(relativePosixPath);

        foreach (var segment in relativePosixPath.Split('/'))
        {
            if (IgnoredSegments.Contains(segment))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Computes the repo-relative, POSIX-separated path of <paramref name="fullPath"/> under
    /// <paramref name="root"/>.
    /// </summary>
    /// <param name="root">Absolute repository root.</param>
    /// <param name="fullPath">Absolute path inside the root.</param>
    /// <param name="relative">When this method returns, contains the relative path using POSIX separators. This parameter is treated as uninitialized.</param>
    /// <returns><see langword="true" /> when <paramref name="fullPath"/> is strictly inside <paramref name="root"/>; otherwise, <see langword="false" />.</returns>
    public static bool TryGetRelativePosixPath(string root, string fullPath, out string relative)
    {
        relative = string.Empty;

        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath))
        {
            return false;
        }

        string normalizedRoot;
        string normalizedFull;

        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            normalizedFull = Path.GetFullPath(fullPath);
        }
        catch (Exception)
        {
            return false;
        }

        var prefix = normalizedRoot + Path.DirectorySeparatorChar;

        if (!normalizedFull.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        relative = normalizedFull[prefix.Length..].Replace('\\', '/');
        return true;
    }

    // ---------------------------------------------------------------------- loop

    private async Task LoopAsync(IProgress<HookRunResult>? progress, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var wait = ComputeWait();

            try
            {
                if (wait is null)
                {
                    await _signal.WaitAsync(ct);
                }
                else if (wait > TimeSpan.Zero)
                {
                    await _signal.WaitAsync(wait.Value, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            await FlushDueAsync(progress, ct);
        }
    }

    /// <summary>Time until the earliest pending pipeline is due, or null when nothing is pending.</summary>
    private TimeSpan? ComputeWait()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return null;
            }

            var earliest = DateTimeOffset.MaxValue;

            foreach (var entry in _pending.Values)
            {
                var due = entry.LastEventAt + _debounce;

                if (due < earliest)
                {
                    earliest = due;
                }
            }

            return earliest - DateTimeOffset.UtcNow;
        }
    }

    private async Task FlushDueAsync(IProgress<HookRunResult>? progress, CancellationToken ct)
    {
        List<PendingRun> due;

        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            due = [];

            foreach (var (fileName, entry) in _pending.ToArray())
            {
                if (now - entry.LastEventAt >= _debounce)
                {
                    due.Add(entry);
                    _pending.Remove(fileName);
                }
            }
        }

        foreach (var entry in due)
        {
            ct.ThrowIfCancellationRequested();
            await RunPipelineAsync(entry.PipelineFileName, entry.RelativePath, progress, ct);
        }
    }

    private void Signal()
    {
        // Coalesce wake-ups: one pending token is enough to force the loop to recompute.
        if (_signal.CurrentCount == 0)
        {
            _signal.Release();
        }
    }

    private async Task RunInitialAsync(IProgress<HookRunResult>? progress, CancellationToken ct)
    {
        foreach (var pipeline in _watchPipelines)
        {
            ct.ThrowIfCancellationRequested();
            await RunPipelineAsync(pipeline.FileName, relativePath: null, progress, ct);
        }
    }

    private async Task RunPipelineAsync(
        string pipelineFile,
        string? relativePath,
        IProgress<HookRunResult>? progress,
        CancellationToken ct)
    {
        HookRunOutcome outcome;

        try
        {
            outcome = await _hookRunner.RunEventAsync(
                _root!,
                TriggerEvent.Watch,
                new HookEventContext(Note: relativePath),
                pipelineFile,
                detach: false,
                notify: _notify,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A run must never kill the watch loop; surface it as a failed result.
            SafeReport(progress, new HookRunResult(pipelineFile, null, RunStatus.Failed, Skipped: false, Error: exception.Message));
            return;
        }

        foreach (var result in outcome.Results)
        {
            SafeReport(progress, result);
        }
    }

    /// <summary>
    /// Reports a result without ever letting a failing sink (e.g. a renderer that throws on malformed
    /// markup) escape and terminate the long-running watch loop.
    /// </summary>
    private static void SafeReport(IProgress<HookRunResult>? progress, HookRunResult result)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            progress.Report(result);
        }
        catch (Exception)
        {
            // A reporting failure is never fatal to watching; drop the report and keep going.
        }
    }

    private async Task<List<WatchPipeline>> DiscoverWatchPipelinesAsync(string railwayPath, string? pipelineFile)
    {
        var candidates = await _pipelineSource.GetPipelinesAsync(railwayPath);
        var watchPipelines = new List<WatchPipeline>();

        foreach (var candidate in candidates)
        {
            if (pipelineFile is not null
                && !string.Equals(Path.GetFileName(candidate.FileName), pipelineFile, StringComparison.Ordinal))
            {
                continue;
            }

            if (candidate.Pipeline is null)
            {
                continue;
            }

            var globs = candidate.Pipeline.Triggers
                .Where(trigger => trigger.Event == TriggerEvent.Watch)
                .SelectMany(trigger => trigger.WatchPaths.Count > 0 ? trigger.WatchPaths : WatchDefaults.MatchEverything)
                .ToArray();

            if (globs.Length == 0)
            {
                continue;
            }

            watchPipelines.Add(new WatchPipeline(candidate.FileName, globs));
        }

        return watchPipelines;
    }

    // ---------------------------------------------------------------------- filters

    private bool IsGitIgnored(string relativePosixPath)
    {
        if (_root is null)
        {
            return false;
        }

        try
        {
            var result = _systemCommandRunner.Run(
                "git",
                ["check-ignore", "--quiet", "--", relativePosixPath],
                workingDirectory: _root);

            // 0 = ignored, 1 = not ignored, any other code (e.g. 127 git missing) = degrade to "not ignored".
            return result.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed record WatchPipeline(string FileName, IReadOnlyList<string> Globs);

    private sealed record PendingRun(string PipelineFileName, string RelativePath, DateTimeOffset LastEventAt);
}