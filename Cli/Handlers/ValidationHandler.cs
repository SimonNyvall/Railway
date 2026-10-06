using Cli.Domain;
using Cli.Handlers.Interfaces;
using Cli.Helpers;
using Cli.Services;
using Cli.Services.Interfaces;
using Spectre.Console;
namespace Cli.Handlers;

public class ValidationHandler : IValidationHandler
{
	private readonly IValidateService _validateService;

    public ValidationHandler()
    {
    	_validateService = LifetimeService.Get<IValidateService>();
    }

    public async Task Handle(string path)
	{
		if (!IsFileYaml(path))
		{
			throw new ArgumentException("Not a yaml file.");
		}

		if (!Path.Exists(path))
		{
			throw new ArgumentException("Not a valid path.");
		}

		var pipeline = await PipelineHelper.Parse(path);

		var errors = (await _validateService.IsValid(pipeline)).ToList();

		if (errors.Count == 0)
		{
			PrintValidOutput(pipeline);
			return;
		}

        PrintInvalidOutput(errors);
	}

	private static bool IsFileYaml(string path)
	{
		return path.EndsWith(".yml") || path.EndsWith(".yaml");
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