using ModelContextProtocol;
using Railway.Engine.Domain;

namespace Railway.Mcp.Tools;

/// <summary>
/// Adapts Engine <see cref="RunEvent"/> progress to MCP progress notifications.
/// </summary>
/// <remarks>
/// Milestone events are always reported as progress messages. Standard output and error
/// events are only forwarded when <c>includeOutput</c> is enabled, so chatty commands do
/// not flood the client. Progress values increase monotonically; the total is unknown and
/// therefore left <see langword="null"/>.
/// </remarks>
internal sealed class RunEventProgressAdapter : IProgress<RunEvent>
{
    private readonly IProgress<ProgressNotificationValue>? _progress;
    private readonly bool _includeOutput;
    private readonly bool _verbose;
    private int _counter;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunEventProgressAdapter"/> class.
    /// </summary>
    /// <param name="progress">The MCP progress sink, or <see langword="null"/> to discard notifications.</param>
    /// <param name="includeOutput"><see langword="true"/> to forward standard output and error lines; otherwise, <see langword="false"/>.</param>
    /// <param name="verbose"><see langword="true"/> to include step commands in milestone messages; otherwise, <see langword="false"/>.</param>
    public RunEventProgressAdapter(
        IProgress<ProgressNotificationValue>? progress,
        bool includeOutput,
        bool verbose)
    {
        _progress = progress;
        _includeOutput = includeOutput;
        _verbose = verbose;
    }

    /// <inheritdoc/>
    public void Report(RunEvent @event)
    {
        var message = Format(@event);
        if (message is null)
        {
            return;
        }

        _progress?.Report(new ProgressNotificationValue
        {
            Progress = Interlocked.Increment(ref _counter),
            Message = message,
        });
    }

    private string? Format(RunEvent @event) => @event.Kind switch
    {
        RunEventKind.RunStarted => $"Run {@event.RunId} started.",
        RunEventKind.JobStarted => $"Job '{@event.JobName}' started.",
        RunEventKind.StepStarted => _verbose && !string.IsNullOrWhiteSpace(@event.Text)
            ? $"Step '{@event.StepName}' started: {@event.Text}"
            : $"Step '{@event.StepName}' started.",
        RunEventKind.StepCompleted =>
            $"Step '{@event.StepName}' completed with exit code {@event.ExitCode}.",
        RunEventKind.JobCompleted =>
            $"Job '{@event.JobName}' completed with status {@event.Status} (exit code {@event.ExitCode}).",
        RunEventKind.RunCompleted =>
            $"Run completed with status {@event.Status} (exit code {@event.ExitCode}).",
        RunEventKind.StandardOutput when _includeOutput => @event.Text,
        RunEventKind.StandardError when _includeOutput => $"[stderr] {@event.Text}",
        _ => null,
    };
}
