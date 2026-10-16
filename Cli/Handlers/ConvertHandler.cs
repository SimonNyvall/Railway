using Railway.Engine.Domain;
using Cli.Handlers.Interfaces;
using Cli.Rendering;
using Railway.Engine.Helpers;
using Railway.Engine.Orchestration.Interfaces;
using Spectre.Console;

namespace Cli.Handlers;

/// <inheritdoc/>
public class ConvertHandler : IConvertHandler
{
    private readonly IPipelineConverter _pipelineConverter;

    public ConvertHandler(IPipelineConverter pipelineConverter)
    {
        _pipelineConverter = pipelineConverter;
    }

    /// <inheritdoc/>
    public async Task<bool> Handle(string sourcePath, SourceFormat? format, string? outputName, bool force)
    {
        var cwd = Directory.GetCurrentDirectory();

        var result = await ConvertAsync(sourcePath, format, cwd, outputName, force);

        if (result.Status == ConversionStatus.FormatUndetermined || result.Format is null)
        {
            if (!AnsiConsole.Profile.Capabilities.Interactive)
            {
                AnsiConsole.MarkupLine(
                    "[red]Cannot detect pipeline format; pass --format github|gitlab|azure[/]");
                return false;
            }

            var chosen = AnsiConsole.Prompt(
                new SelectionPrompt<SourceFormat>()
                    .Title("Select the source pipeline format:")
                    .AddChoices(Enum.GetValues<SourceFormat>()));

            result = await ConvertAsync(sourcePath, chosen, cwd, outputName, force);
        }

        if (result.Error is not null)
        {
            AnsiConsole.MarkupLine($"[red]✗ Conversion failed:[/] {result.Error}");
            throw new InvalidOperationException(result.Error);
        }

        if (result.ValidationErrors.Count > 0)
        {
            PrintWarnings(result.Warnings);
            PrintValidationErrors(result.ValidationErrors);

            var outputPath = result.OutputPath!;
            var keep = !AnsiConsole.Profile.Capabilities.Interactive
                || AnsiConsole.Confirm("Keep the file anyway?", defaultValue: false);

            if (keep)
            {
                AnsiConsole.MarkupLine($"[yellow]Kept {outputPath}[/]");
            }
            else
            {
                File.Delete(outputPath);
                AnsiConsole.MarkupLine($"[yellow]Deleted {outputPath}[/]");
            }

            return false;
        }

        var pipeline = await PipelineHelper.Parse(result.OutputPath!);

        AnsiConsole.MarkupLine($"[green]✓ Converted + validated → {result.OutputPath}[/]");
        PrintWarnings(result.Warnings);
        PrintPipeline(pipeline);

        return true;
    }

    private async Task<ConversionResult> ConvertAsync(
        string sourcePath,
        SourceFormat? format,
        string cwd,
        string? outputName,
        bool force)
    {
        try
        {
            return await _pipelineConverter.ConvertAsync(
                sourcePath,
                format,
                cwd,
                outputName,
                force,
                CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            // Raised when the target file exists and overwriting was not allowed.
            AnsiConsole.MarkupLine($"[red]✗ {ex.Message}[/]");
            throw;
        }
    }

    private static void PrintWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold yellow]Warnings:[/]");

        foreach (var warning in warnings)
        {
            AnsiConsole.MarkupLine($"  [yellow]![/] {warning}");
        }

        AnsiConsole.WriteLine();
    }

    private static void PrintValidationErrors(IReadOnlyList<string> errors)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[red]✗ Converted pipeline failed validation[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold red]Errors:[/]");

        foreach (var error in errors)
        {
            AnsiConsole.MarkupLine($"  [red]✗[/] {error}");
        }
    }

    private static void PrintPipeline(Pipeline pipeline)
    {
        AnsiConsole.MarkupLine($"[bold]Pipeline:[/] {pipeline.Name}");
        AnsiConsole.WriteLine();

        PipelineTreeRenderer.RenderJobs(pipeline);
    }
}
