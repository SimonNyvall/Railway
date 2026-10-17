using Railway.Engine.Domain;
using Railway.Engine.Orchestration.Interfaces;
using Spectre.Console;

namespace Cli.Rendering;

/// <summary>
/// Shared renderer for hook run results (used by hooks run and hooks watch).
/// </summary>
public static class HookRunResultRenderer
{
    public static void Render(HookRunResult result, bool includeTimestamp)
    {
        Render(result, includeTimestamp, indent: false);
    }

    public static void Render(HookRunResult result, bool includeTimestamp, bool indent)
    {
        var prefix = string.Empty;
        if (includeTimestamp)
        {
            prefix = $"[dim]{DateTimeOffset.Now.ToString("HH:mm:ss")}[/] ";
        }

        var indentPrefix = indent ? "  " : string.Empty;
        var pipeline = Markup.Escape(result.PipelineFileName);

        if (result.Error is not null)
        {
            AnsiConsole.MarkupLine($"{prefix}{indentPrefix}[red]✗[/] [cyan]{pipeline}[/] - {Markup.Escape(result.Error)}");
            return;
        }

        if (result.Skipped)
        {
            AnsiConsole.MarkupLine(
                $"{prefix}{indentPrefix}[dim]⊘[/] [cyan]{pipeline}[/] - skipped (another run is in progress)");
            return;
        }

        if (result.Detached)
        {
            AnsiConsole.MarkupLine($"{prefix}{indentPrefix}[yellow]⟳[/] [cyan]{pipeline}[/] - detached");
            return;
        }

        var status = result.Status switch
        {
            RunStatus.Succeeded => "[green]✓[/] succeeded",
            RunStatus.Failed => "[red]✗[/] failed",
            RunStatus.Running => "[yellow]⟳[/] running",
            RunStatus.Cancelled => "[dim]⊘[/] cancelled",
            RunStatus.Skipped => "[dim]⊘[/] skipped",
            _ => $"[dim]{result.Status}[/]",
        };

        var runId = result.RunId is { } id ? $" [dim](#{id})[/]" : string.Empty;
        AnsiConsole.MarkupLine($"{prefix}{indentPrefix}{status} [cyan]{pipeline}[/]{runId}");
    }
}