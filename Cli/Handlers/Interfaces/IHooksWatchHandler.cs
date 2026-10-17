namespace Cli.Handlers.Interfaces;

/// <summary>
/// Renders the result of <c>railway hooks watch</c>: a live line per run as the pipelines execute,
/// until the supplied token is cancelled.
/// </summary>
public interface IHooksWatchHandler
{
    /// <summary>
    /// Starts a foreground watch session and renders each run as it completes.
    /// </summary>
    /// <param name="pipeline">Optional single pipeline file name filter.</param>
    /// <param name="debounce">Per-pipeline debounce window, or null for the Engine default.</param>
    /// <param name="initial">When true, each matching pipeline runs once immediately on start.</param>
    /// <param name="path">Repository root (or any path inside it); null uses the current directory.</param>
    /// <param name="verbose">When true, renders extra detail.</param>
    /// <param name="ct">Cancellation token; cancellation stops the watch cleanly.</param>
    Task Handle(
        string? pipeline,
        TimeSpan? debounce,
        bool initial,
        string? path,
        bool verbose,
        CancellationToken ct = default);
}
