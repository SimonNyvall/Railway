using Cli.Handlers.Interfaces;
using Railway.Engine.Orchestration.Interfaces;
using Spectre.Console;

namespace Cli.Handlers;

/// <summary>
/// Renders the result of <c>railway hooks sync</c>: the diff between the desired and installed
/// hook artifacts (added, removed, updated — including schedule entries) and schedule errors.
/// </summary>
public class HooksSyncHandler : IHooksSyncHandler
{
    private readonly IHookInstaller _installer;

    public HooksSyncHandler(IHookInstaller installer)
    {
        _installer = installer;
    }

    /// <inheritdoc />
    public async Task<bool> Handle(string? path, bool verbose)
    {
        var result = await _installer.Sync(path ?? Directory.GetCurrentDirectory());

        if (result.InSync && result.Errors is not { Count: > 0 })
        {
            AnsiConsole.MarkupLine("[green]✓ Hooks already in sync.[/]");
            return true;
        }

        if (result.Added.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold]Added:[/]");
            foreach (var item in result.Added)
            {
                AnsiConsole.MarkupLine($"  [green]+[/] {item}");
            }
        }

        if (result.Removed.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold]Removed:[/]");
            foreach (var item in result.Removed)
            {
                AnsiConsole.MarkupLine($"  [red]-[/] {item}");
            }
        }

        if (result.Updated.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold]Updated:[/]");
            foreach (var item in result.Updated)
            {
                AnsiConsole.MarkupLine($"  [yellow]~[/] {item}");
            }
        }

        foreach (var error in result.Errors ?? [])
        {
            AnsiConsole.MarkupLine($"  [red]✗[/] {error}");
        }

        if (result.Errors is { Count: > 0 })
        {
            AnsiConsole.MarkupLine("[red]✗ Schedule sync failed.[/]");
            return false;
        }

        AnsiConsole.MarkupLine($"[green]✓ Hooks synced[/] in {result.RepositoryPath}");
        return true;
    }
}
