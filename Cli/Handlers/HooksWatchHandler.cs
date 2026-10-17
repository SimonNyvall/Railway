using Cli.Handlers.Interfaces;
using Cli.Rendering;
using Railway.Engine.Orchestration.Interfaces;
using Spectre.Console;

namespace Cli.Handlers;

/// <summary>
/// Renders <c>railway hooks watch</c>: for each pipeline run the watcher reports, a line in the same
/// style as <c>hooks run</c> (succeeded/failed/skipped/detached). Blocks until cancelled.
/// </summary>
public class HooksWatchHandler : IHooksWatchHandler
{
    private readonly IHookWatcher _watcher;

    public HooksWatchHandler(IHookWatcher watcher)
    {
        _watcher = watcher;
    }

    /// <inheritdoc />
    public async Task Handle(
        string? pipeline,
        TimeSpan? debounce,
        bool initial,
        string? path,
        bool verbose,
        CancellationToken ct = default)
    {
        var repositoryPath = path ?? Directory.GetCurrentDirectory();

        AnsiConsole.MarkupLine(
            $"[dim]Watching {Markup.Escape(repositoryPath)} for changes; press Ctrl+C to stop.[/]");

        var options = new HookWatchOptions(pipeline, debounce, initial, Notify: true);
        var progress = new InlineRunProgress();

        await _watcher.WatchAsync(repositoryPath, options, progress, ct);

        AnsiConsole.MarkupLine("[dim]Stopped watching.[/]");
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/> so run lines keep their execution order.</summary>
    private sealed class InlineRunProgress : IProgress<HookRunResult>
    {
        public void Report(HookRunResult value) => RenderResult(value);
    }

    private static void RenderResult(HookRunResult result)
    {
        Cli.Rendering.HookRunResultRenderer.Render(result, includeTimestamp: true, indent: false);
    }
}
