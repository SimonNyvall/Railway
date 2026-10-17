using Cli.Handlers.Interfaces;
using Railway.Engine.Orchestration.Interfaces;
using Spectre.Console;

namespace Cli.Handlers;

/// <summary>
/// Renders the result of <c>railway hooks uninstall</c>: artifacts removed or restored, warnings for
/// foreign hooks left in place, and a "nothing to remove" note when the repository is already clean.
/// </summary>
public class HooksUninstallHandler : IHooksUninstallHandler
{
    private readonly IHookInstaller _installer;

    public HooksUninstallHandler(IHookInstaller installer)
    {
        _installer = installer;
    }

    /// <inheritdoc />
    public async Task Handle(string? path, bool purge, bool verbose)
    {
        var result = await _installer.Uninstall(path ?? Directory.GetCurrentDirectory(), purge);

        if (!result.AnyRemoved && !result.PurgedLogs && result.Warnings.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]Nothing to remove.[/]");
            return;
        }

        foreach (var artifact in result.Artifacts)
        {
            RenderArtifact(artifact, verbose);
        }

        if (result.PurgedLogs)
        {
            AnsiConsole.MarkupLine("  [green]✓[/] removed [cyan].railway/logs[/]");
        }

        foreach (var warning in result.Warnings)
        {
            AnsiConsole.MarkupLine($"  [yellow]![/] {warning}");
        }

        AnsiConsole.MarkupLine($"[green]✓ Hooks uninstalled[/] from {result.RepositoryPath}");
    }

    private static void RenderArtifact(HookArtifact artifact, bool verbose)
    {
        var detail = verbose ? $" [dim]({artifact.Path})[/]" : string.Empty;

        var markup = artifact.Action switch
        {
            HookArtifactAction.Removed => $"[green]✓[/] removed [cyan]{artifact.Name}[/]",
            HookArtifactAction.Restored => $"[green]✓[/] restored [cyan]{artifact.Name}[/] [dim](original hook)[/]",
            _ => $"[dim]· {artifact.Name} ({artifact.Action})[/]",
        };

        AnsiConsole.MarkupLine($"  {markup}{detail}");
    }
}
