using Railway.Engine.Domain;
using Cli.Handlers.Interfaces;
using Railway.Engine.Orchestration;
using Railway.Engine.Orchestration.Interfaces;
using Spectre.Console;

namespace Cli.Handlers;

public class ValidationHandler : IValidationHandler
{
    private readonly IPipelineValidator _pipelineValidator;

    public ValidationHandler(IPipelineValidator pipelineValidator)
    {
        _pipelineValidator = pipelineValidator;
    }

    public async Task<bool> Handle(string path)
    {
        var result = await _pipelineValidator.ValidateAsync(path);

        if (result.Errors.Count == 0)
        {
            PrintValidOutput(result.Pipeline);
            return true;
        }

        PrintInvalidOutput(result.Errors);
        return false;
    }

    private static void PrintValidOutput(Pipeline pipeline)
    {
        AnsiConsole.MarkupLine($"[bold]Pipeline:[/] {pipeline.Name}");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[green]✓ Pipeline is valid[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine($"[bold]Jobs:[/] {pipeline.Jobs.Count}");
        AnsiConsole.WriteLine();

        foreach (var (name, job) in pipeline.Jobs)
        {
            AnsiConsole.MarkupLine($"  [cyan]{name}[/]");

            if (job.DependsOn.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"    [dim]Depends on:[/] {string.Join(", ", job.DependsOn)}"
                );
            }

            AnsiConsole.MarkupLine($"    [dim]Steps:[/] {job.Steps.Count}");

            foreach (var step in job.Steps)
            {
                var stepName = step.Name ?? "Unnamed step";

                AnsiConsole.MarkupLine($"      [green]✓[/] {stepName}");

                if (!string.IsNullOrWhiteSpace(step.Run))
                {
                    AnsiConsole.MarkupLine($"        [dim]{step.Run}[/]");
                }
            }

            AnsiConsole.WriteLine();
        }
    }

    private static void PrintInvalidOutput(IEnumerable<string> errors)
    {
        AnsiConsole.MarkupLine("[red]✗ Pipeline is invalid[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold red]Errors:[/]");

        foreach (var error in errors)
        {
            AnsiConsole.MarkupLine($"  [red]✗[/] {error}");
        }
    }
}
