using Cli.Domain;
using Cli.Handlers.Interfaces;
using Cli.Helpers;
using Cli.Services;
using Cli.Services.Interfaces;
using Spectre.Console;

namespace Cli.Handlers;

public class DashboardHandler : IDashboardHandler
{
    private readonly IDashboardService _dashboardService;

    public DashboardHandler() : this(LifetimeService.Get<IDashboardService>())
    {
    }

    public DashboardHandler(IDashboardService dashboardService)
    {
        _dashboardService = dashboardService;
    }

    public async Task Handle()
    {
        var view = await _dashboardService.GetView();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Repository:[/] {view.RepositoryName}");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold]Pipelines:[/]");
        if (view.Pipelines.Count == 0)
        {
            AnsiConsole.MarkupLine("  [dim]No pipelines configured[/]");
        }
        else
        {
            foreach (var pipeline in view.Pipelines)
            {
                if (pipeline.IsValid)
                {
                    AnsiConsole.MarkupLine($"  [green]✓[/] [cyan]{pipeline.FileName}[/] - name: {pipeline.Name}");
                }
                else
                {
                    AnsiConsole.MarkupLine($"  [red]✗[/] [red]{pipeline.FileName}[/] - name: [dim](invalid)[/]");
                    foreach (var error in pipeline.Errors)
                    {
                        AnsiConsole.MarkupLine($"    [red]✗[/] {error}");
                    }
                }
            }
        }
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold]Recent Runs:[/]");
        if (view.RecentRuns.Count == 0)
        {
            AnsiConsole.MarkupLine("  [dim]No runs yet[/]");
        }
        else
        {
            foreach (var run in view.RecentRuns)
            {
                var statusMarkup = run.Status switch
                {
                    RunStatus.Succeeded => "[green]✓ Succeeded[/]",
                    RunStatus.Failed => $"[red]✗ Failed (exit {run.ExitCode})[/]",
                    RunStatus.Running => "[yellow]⟳ Running[/]",
                    RunStatus.Cancelled => "[dim]⊘ Cancelled[/]",
                    _ => $"[dim]{run.Status}[/]"
                };

                AnsiConsole.MarkupLine($"  {run.PipelineFileName} - {run.PipelineName} - {statusMarkup} - {run.StartedAt:yyyy-MM-dd HH:mm:ss}");
            }
        }
    }
}