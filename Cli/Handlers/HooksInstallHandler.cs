using Cli.Handlers.Interfaces;
using Railway.Engine.Orchestration.Interfaces;
using Spectre.Console;

namespace Cli.Handlers;

/// <summary>
/// Renders the result of <c>railway hooks install</c>: one line per artifact written or chained
/// (including schedule entries), warnings for chained foreign hooks, and schedule errors/warnings.
/// </summary>
public class HooksInstallHandler : IHooksInstallHandler
{
    private readonly IHookInstaller _installer;

    public HooksInstallHandler(IHookInstaller installer)
    {
        _installer = installer;
    }

    /// <inheritdoc />
    public async Task<bool> Handle(string? path, bool verbose)
    {
        var result = await _installer.Install(path ?? Directory.GetCurrentDirectory());

        if (result.Artifacts.Count == 0 && result.ScheduleErrors is not { Count: > 0 })
        {
            AnsiConsole.MarkupLine("[red]✗ No git-triggering pipelines found.[/]");
            AnsiConsole.MarkupLine(
                "[dim]Add a 'triggers:' entry (e.g. 'post-commit' or 'schedule') to a pipeline under .railway/pipelines/ and retry.[/]");
            return false;
        }

        foreach (var artifact in result.Artifacts)
        {
            RenderArtifact(artifact, verbose);
        }

        foreach (var error in result.ScheduleErrors ?? [])
        {
            AnsiConsole.MarkupLine($"  [red]✗[/] {error}");
        }

        foreach (var warning in result.ScheduleWarnings ?? [])
        {
            AnsiConsole.MarkupLine($"  [yellow]![/] {warning}");
        }

        if (result.GitignoreChanged)
        {
            AnsiConsole.MarkupLine("  [green]✓[/] [cyan].gitignore[/] block updated");
        }

        if (result.ScheduleErrors is { Count: > 0 })
        {
            AnsiConsole.MarkupLine("[red]✗ Schedule materialization failed.[/]");
            return false;
        }

        if (result.AnyInstalled)
        {
            AnsiConsole.MarkupLine($"[green]✓ Hooks installed[/] in {result.RepositoryPath}");
        }
        else
        {
            AnsiConsole.MarkupLine("[green]✓ Hooks already up to date[/]");
        }

        return true;
    }

    private static void RenderArtifact(HookArtifact artifact, bool verbose)
    {
        var detail = verbose ? $" [dim]({artifact.Path})[/]" : string.Empty;

        var markup = artifact.Action switch
        {
            HookArtifactAction.Created => $"[green]✓[/] created [cyan]{artifact.Name}[/]",
            HookArtifactAction.Updated => $"[green]✓[/] updated [cyan]{artifact.Name}[/]",
            HookArtifactAction.Chained =>
                $"[yellow]![/] chained existing [cyan]{artifact.Name}[/] [dim](kept at {artifact.Detail})[/]",
            HookArtifactAction.Removed => $"[dim]⊘ removed {artifact.Name}[/]",
            HookArtifactAction.Restored => $"[dim]↻ restored {artifact.Name}[/]",
            HookArtifactAction.Unchanged => $"[dim]· {artifact.Name} already installed[/]",
            _ => $"[dim]{artifact.Name} ({artifact.Action})[/]",
        };

        AnsiConsole.MarkupLine($"  {markup}{detail}");
    }
}
