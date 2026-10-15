using Railway.Engine.Domain;
using Spectre.Console;

namespace Cli.Rendering;

public sealed class SpectreRunProgress : IProgress<RunEvent>
{
    private readonly bool _verbose;

    public SpectreRunProgress(bool verbose)
    {
        _verbose = verbose;
    }

    public void Report(RunEvent e)
    {
        switch (e.Kind)
        {
            case RunEventKind.StepStarted:
                var stepName = e.StepName ?? "Unnamed step";
                var bannerWidth = stepName.Length + 10;
                var topBanner = "┌─ Step: " + stepName + " " + new string('─', Math.Max(0, bannerWidth - stepName.Length - 9)) + "┐";
                var bottomBanner = "└" + new string('─', bannerWidth) + "┘";
                AnsiConsole.MarkupLine($"[blue]{Markup.Escape(topBanner)}[/]");
                if (_verbose)
                {
                    AnsiConsole.MarkupLine($"[dim]$ {Markup.Escape(e.Text ?? string.Empty)}[/]");
                }
                AnsiConsole.MarkupLine($"[blue]{Markup.Escape(bottomBanner)}[/]");
                break;

            case RunEventKind.StandardOutput:
                AnsiConsole.MarkupLine((e.Text ?? string.Empty).EscapeMarkup());
                break;

            case RunEventKind.StandardError:
                AnsiConsole.MarkupLine($"[red]{(e.Text ?? string.Empty).EscapeMarkup()}[/]");
                break;
        }
    }
}
