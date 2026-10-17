using Cli.Handlers.Interfaces;
using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Engine.Services.Interfaces;
using Spectre.Console;

namespace Cli.Handlers;

/// <summary>
/// Renders the result of <c>railway hooks status</c>: the resolved hooks directory, whether the
/// <c>.gitignore</c> block is configured, the selected scheduler, and per pipeline its triggers
/// (with git hook state, schedule state/entry, validation errors), lock state and most recent run.
/// </summary>
public class HooksStatusHandler : IHooksStatusHandler
{
    private readonly IHookInstaller _installer;

    public HooksStatusHandler(IHookInstaller installer)
    {
        _installer = installer;
    }

    /// <inheritdoc />
    public async Task Handle(string? path, bool verbose)
    {
        var report = await _installer.Status(path ?? Directory.GetCurrentDirectory());

        AnsiConsole.MarkupLine($"[bold]Repository:[/] {report.RepositoryPath}");
        AnsiConsole.MarkupLine($"[bold]Git hooks dir:[/] {report.GitHooksDirectory ?? "[dim](unresolved)[/]"}");
        AnsiConsole.MarkupLine(
            $"[bold].gitignore configured:[/] {(report.GitignoreConfigured ? "[green]yes[/]" : "[yellow]no[/]")}");

        if (report.Scheduler is not null)
        {
            AnsiConsole.MarkupLine($"[bold]Scheduler:[/] {report.Scheduler}");
        }

        AnsiConsole.WriteLine();

        if (report.Pipelines.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No pipelines configured.[/]");
            return;
        }

        foreach (var pipeline in report.Pipelines)
        {
            RenderPipeline(pipeline, verbose);
        }
    }

    private static void RenderPipeline(PipelineHookStatus pipeline, bool verbose)
    {
        AnsiConsole.MarkupLine($"[bold cyan]{pipeline.PipelineFileName}[/]");

        foreach (var error in pipeline.Errors)
        {
            AnsiConsole.MarkupLine($"  [red]✗[/] {error}");
        }

        if (pipeline.Triggers.Count == 0)
        {
            AnsiConsole.MarkupLine("  [dim]no triggers declared[/]");
        }
        else
        {
            foreach (var trigger in pipeline.Triggers)
            {
                RenderTrigger(trigger, verbose);
            }
        }

        AnsiConsole.MarkupLine(
            pipeline.LockHeld
                ? "  [yellow]lock:[/] held (a run may be in progress)"
                : "  [dim]lock: free[/]");

        RenderLastRun(pipeline.LastRun);
        AnsiConsole.WriteLine();
    }

    private static void RenderTrigger(TriggerStatus trigger, bool verbose)
    {
        var keyword = HookScriptHelper.GetEventKeyword(trigger.Event);
        var filters = DescribeFilters(trigger);

        if (trigger.Event == TriggerEvent.Schedule)
        {
            var next = trigger.NextOccurrence is { } occurrence
                ? $" [dim]next {occurrence:yyyy-MM-dd HH:mm:ss}[/]"
                : string.Empty;
            var state = trigger.ScheduleState is { } scheduleState
                ? $" {DescribeScheduleState(scheduleState)}"
                : string.Empty;
            AnsiConsole.MarkupLine($"  [cyan]{keyword}[/]{filters}{state}{next}");

            if (trigger.ScheduleEntry is not null)
            {
                AnsiConsole.MarkupLine($"      [dim]{trigger.ScheduleEntry}[/]");
            }

            if (trigger.ScheduleError is not null)
            {
                AnsiConsole.MarkupLine($"      [red]✗[/] {trigger.ScheduleError}");
            }
        }
        else if (trigger.Event == TriggerEvent.Watch)
        {
            AnsiConsole.MarkupLine($"  [cyan]{keyword}[/]{filters}");
        }
        else
        {
            AnsiConsole.MarkupLine($"  [cyan]{keyword}[/]{filters} {DescribeHookState(trigger.HookState)}");
            if (verbose && trigger.HookPath is not null)
            {
                AnsiConsole.MarkupLine($"      [dim]{trigger.HookPath}[/]");
            }

            if (trigger.BackupPath is not null)
            {
                AnsiConsole.MarkupLine($"      [dim]chaining {trigger.BackupPath}[/]");
            }
        }

        foreach (var error in trigger.Errors)
        {
            AnsiConsole.MarkupLine($"      [red]✗[/] {error}");
        }
    }

    private static string DescribeFilters(TriggerStatus trigger)
    {
        if (trigger.Event == TriggerEvent.PrePush)
        {
            var parts = new List<string>();
            if (trigger.Branches.Count > 0)
            {
                parts.Add($"branches: {string.Join(", ", trigger.Branches)}");
            }

            if (trigger.Tags.Count > 0)
            {
                parts.Add($"tags: {string.Join(", ", trigger.Tags)}");
            }

            return parts.Count == 0 ? string.Empty : $" [dim]({string.Join("; ", parts)})[/]";
        }

        if (trigger.Event == TriggerEvent.Schedule && !string.IsNullOrWhiteSpace(trigger.Cron))
        {
            return $" [dim]({trigger.Cron})[/]";
        }

        if (trigger.Event == TriggerEvent.Watch && trigger.WatchPaths.Count > 0)
        {
            return $" [dim]({string.Join(", ", trigger.WatchPaths)})[/]";
        }

        return string.Empty;
    }

    private static string DescribeHookState(GitHookState? state) => state switch
    {
        GitHookState.Installed => "[green]installed[/]",
        GitHookState.ForeignChained => "[green]installed[/] [yellow](chaining foreign hook)[/]",
        GitHookState.Missing => "[yellow]missing[/]",
        GitHookState.Drifted => "[yellow]drifted[/]",
        GitHookState.Foreign => "[yellow]foreign (not installed)[/]",
        _ => "[dim]not applicable[/]",
    };

    private static string DescribeScheduleState(ScheduleState state) => state switch
    {
        ScheduleState.Installed => "[green]installed[/]",
        ScheduleState.Missing => "[yellow]missing[/]",
        ScheduleState.Drifted => "[yellow]drifted[/]",
        ScheduleState.Unsupported => "[yellow]unsupported on this scheduler[/]",
        _ => $"[dim]{state}[/]",
    };

    private static void RenderLastRun(RunSummary? lastRun)
    {
        if (lastRun is null)
        {
            AnsiConsole.MarkupLine("  [dim]last run: none[/]");
            return;
        }

        var status = lastRun.Status switch
        {
            Railway.Engine.Domain.RunStatus.Succeeded => "[green]succeeded[/]",
            Railway.Engine.Domain.RunStatus.Failed => "[red]failed[/]",
            Railway.Engine.Domain.RunStatus.Running => "[yellow]running[/]",
            Railway.Engine.Domain.RunStatus.Cancelled => "[dim]cancelled[/]",
            Railway.Engine.Domain.RunStatus.Skipped => "[dim]⊘ skipped[/]",
            _ => $"[dim]{lastRun.Status}[/]",
        };

        var trigger = lastRun.Trigger ?? "manual";
        var detail = string.IsNullOrEmpty(lastRun.TriggerDetail) ? string.Empty : $" ({lastRun.TriggerDetail})";

        AnsiConsole.MarkupLine(
            $"  last run: #{lastRun.RunId} {status} [dim]via {trigger}{detail} at {lastRun.StartedAt:yyyy-MM-dd HH:mm:ss}[/]");
    }
}
