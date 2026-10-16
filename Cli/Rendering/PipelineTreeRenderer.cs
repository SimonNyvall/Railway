using Railway.Engine.Domain;
using Spectre.Console;

namespace Cli.Rendering;

/// <summary>
/// Renders the job and step tree of a <see cref="Pipeline"/> to the console.
/// </summary>
/// <remarks>
/// Shared by the <c>validate</c> and <c>convert</c> handlers so both print the same job tree. The caller renders any
/// heading lines (such as a status line) before invoking <see cref="RenderJobs"/>.
/// </remarks>
internal static class PipelineTreeRenderer
{
    /// <summary>
    /// Renders the job count and, for each job, its dependencies and steps.
    /// </summary>
    /// <param name="pipeline">The pipeline whose jobs are rendered.</param>
    public static void RenderJobs(Pipeline pipeline)
    {
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
}
