using Railway.Engine.Domain;

namespace Railway.Engine.Orchestration.Interfaces;

/// <summary>
/// Watches a repository's working tree for file changes and runs the pipelines whose
/// <see cref="TriggerEvent.Watch"/> triggers match, recording run provenance with the
/// <c>watch</c> trigger key. Foreground until the supplied token is cancelled; never writes to
/// stdout.
/// </summary>
/// <remarks>
/// The watcher resolves the repository through <see cref="Helpers.RepositoryHelper"/> using the
/// explicit path it is given (never the current working directory), discovers pipelines through
/// <see cref="IHookPipelineSource"/>, and runs each matching pipeline through <see cref="IHookRunner"/>.
/// File events are coalesced with a per-pipeline trailing debounce so a burst of writes produces a
/// single run per pipeline.
/// </remarks>
public interface IHookWatcher
{
    /// <summary>
    /// Watches <paramref name="repositoryPath"/> until <paramref name="ct"/> is cancelled, running the
    /// matching watch pipelines.
    /// </summary>
    /// <param name="repositoryPath">Repository root or any path inside it (never the current directory).</param>
    /// <param name="options">Watch session options (pipeline filter, debounce, initial run, notifications).</param>
    /// <param name="progress">Optional sink that receives one result per pipeline run as it completes.</param>
    /// <param name="ct">Cancellation token; cancellation is treated as a clean shutdown.</param>
    /// <returns>A task that completes when the watch loop stops.</returns>
    /// <exception cref="NotARailwayRepositoryException">When <paramref name="repositoryPath"/> is not inside a railway repository.</exception>
    Task WatchAsync(
        string repositoryPath,
        HookWatchOptions options,
        IProgress<HookRunResult>? progress,
        CancellationToken ct);
}

/// <summary>
/// Options controlling a single <see cref="IHookWatcher.WatchAsync"/> session.
/// </summary>
/// <param name="PipelineFile">
/// Optional pipeline file name restricting the watch to a single pipeline (matched by file name, like
/// <c>PipelineRunner</c>); null watches every pipeline that declares a <c>watch</c> trigger.
/// </param>
/// <param name="Debounce">
/// Per-pipeline trailing debounce window; null uses <see cref="WatchDefaults.Debounce"/>. A burst of
/// matching events inside the window coalesces into exactly one run per pipeline.
/// </param>
/// <param name="Initial">
/// <see langword="true" /> to run each matching pipeline once immediately on start (with an empty watch
/// context) in addition to the debounced runs; otherwise, <see langword="false" />.
/// </param>
/// <param name="Notify">
/// <see langword="false" /> to suppress notifications regardless of pipeline preferences; otherwise,
/// <see langword="true" />.
/// </param>
public sealed record HookWatchOptions(
    string? PipelineFile = null,
    TimeSpan? Debounce = null,
    bool Initial = false,
    bool Notify = true);

/// <summary>
/// Change notification raised by an <see cref="IWatchEventSource"/> for one file-system event.
/// </summary>
/// <param name="FullPath">Absolute path of the changed entry.</param>
/// <param name="IsDirectory">
/// <see langword="true" /> when the changed entry is a directory (never a run trigger); otherwise,
/// <see langword="false" />.
/// </param>
public sealed record WatchPathChange(string FullPath, bool IsDirectory);

/// <summary>
/// Injectable seam over the operating system's file watcher. The production implementation wraps
/// <see cref="System.IO.FileSystemWatcher"/>; unit tests substitute a fake so the file-event
/// ingestion is deterministic and never touches the real watcher.
/// </summary>
public interface IWatchEventSource : IDisposable
{
    /// <summary>Raised for each created/changed/renamed entry under the watched root.</summary>
    event Action<WatchPathChange>? Changed;

    /// <summary>Starts watching <paramref name="rootPath"/> recursively.</summary>
    /// <param name="rootPath">Absolute repository root to watch.</param>
    void Start(string rootPath);

    /// <summary>Stops watching and releases the underlying watcher (idempotent).</summary>
    void Stop();
}

/// <summary>Well-known defaults for the watch feature.</summary>
public static class WatchDefaults
{
    /// <summary>Default per-pipeline debounce window (<c>1s</c>, overridable with <c>--debounce</c>).</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    /// <summary>Globs a <c>watch</c> trigger falls back to when it declares none (matches every file).</summary>
    public static readonly IReadOnlyList<string> MatchEverything = ["**/*"];
}
