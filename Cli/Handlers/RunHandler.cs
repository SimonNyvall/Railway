using Railway.Engine.Domain;
using Cli.Handlers.Interfaces;
using Cli.Rendering;
using Railway.Engine.Orchestration;
using Railway.Engine.Orchestration.Interfaces;
using Spectre.Console;

namespace Cli.Handlers;

public class RunHandler : IRunHandler
{
    private readonly IPipelineRunner _pipelineRunner;

    public RunHandler(IPipelineRunner pipelineRunner)
    {
        _pipelineRunner = pipelineRunner;
    }

    public async Task Handle(string fileName, bool verbose, string? path = null)
    {
        RunOutcome outcome;

        try
        {
            outcome = await _pipelineRunner.RunAsync(
                path ?? Directory.GetCurrentDirectory(),
                fileName,
                verbose,
                new SpectreRunProgress(verbose));
        }
        catch (NotARailwayRepositoryException)
        {
            throw;
        }
        catch (PipelineFileNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]Pipeline file not found:[/] {ex.FileName}");
            AnsiConsole.MarkupLine($"[dim]Hint: run 'railway list' to see available pipelines[/]");
            throw new FileNotFoundException(ex.Message);
        }
        catch (AmbiguousPipelineException ex)
        {
            AnsiConsole.MarkupLine($"[red]Multiple pipeline files match:[/] {ex.FileName}");
            foreach (var candidatePath in ex.Candidates)
            {
                AnsiConsole.MarkupLine($"  [dim]{candidatePath}[/]");
            }
            throw new InvalidOperationException(ex.Message);
        }
        catch (PipelineInvalidException ex) when (ex.Kind == PipelineInvalidKind.Parse)
        {
            AnsiConsole.MarkupLine($"[red]Failed to parse pipeline:[/] {ex.FileName}");
            foreach (var error in ex.Errors)
            {
                AnsiConsole.MarkupLine($"  [red]✗[/] {error}");
            }
            throw new InvalidOperationException(ex.Message);
        }
        catch (PipelineInvalidException ex) when (ex.Kind == PipelineInvalidKind.Validation)
        {
            AnsiConsole.MarkupLine($"[red]Pipeline is invalid:[/] {ex.FileName}");
            foreach (var error in ex.Errors)
            {
                AnsiConsole.MarkupLine($"  [red]✗[/] {error}");
            }
            throw new InvalidOperationException("Pipeline validation failed");
        }

        var run = outcome.Run;

        if (run.Status == RunStatus.Succeeded)
        {
            AnsiConsole.MarkupLine("[green]✓ Pipeline completed successfully[/]");
        }
        else if (run.Status == RunStatus.Skipped)
        {
            // A skipped run is not a failure: another run held the pipeline lock, so nothing ran.
            AnsiConsole.MarkupLine("[dim]⊘ Pipeline run skipped (another run is in progress)[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗ Pipeline failed with exit code {run.ExitCode}[/]");
            throw new InvalidOperationException($"Pipeline failed with exit code {run.ExitCode}");
        }
    }
}
