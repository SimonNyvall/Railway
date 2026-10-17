using Railway.Engine.Domain;
using Cli.Handlers.Interfaces;
using Railway.Engine.Orchestration.Interfaces;
using Spectre.Console;

namespace Cli.Handlers;

public class DashboardHandler : IDashboardHandler
{
    private readonly IDashboardQuery _dashboardQuery;

    public DashboardHandler(IDashboardQuery dashboardQuery)
    {
        _dashboardQuery = dashboardQuery;
    }

    public async Task Handle()
    {
        var view = await _dashboardQuery.QueryAsync(Directory.GetCurrentDirectory());

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
                    RunStatus.Skipped => "[dim]⊘ Skipped[/]",
                    _ => $"[dim]{run.Status}[/]"
                };

                var trigger = string.IsNullOrEmpty(run.Trigger) ? "manual" : run.Trigger;
                var triggerLabel = string.IsNullOrEmpty(run.TriggerDetail)
                    ? trigger
                    : $"{trigger} ({run.TriggerDetail})";

                AnsiConsole.MarkupLine($"  {run.PipelineFileName} - {run.PipelineName} - {statusMarkup} - {run.StartedAt:yyyy-MM-dd HH:mm:ss} [dim]· {triggerLabel}[/]");
            }
        }
    }
}
