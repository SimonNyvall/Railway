using Railway.Engine.Domain;

namespace Railway.Mcp.Tools;

/// <summary>
/// Maps Engine domain models to the DTOs returned by the MCP tools.
/// </summary>
internal static class ToolDtoMapper
{
    /// <summary>
    /// Maps a discovered pipeline to a <see cref="PipelineSummary"/>.
    /// </summary>
    /// <param name="pipeline">The pipeline to map.</param>
    /// <returns>The mapped summary.</returns>
    public static PipelineSummary ToSummary(this DiscoveredPipeline pipeline) =>
        new(pipeline.FileName, pipeline.Path, pipeline.Name, pipeline.IsValid, pipeline.Errors);

    /// <summary>
    /// Maps a persisted run to a <see cref="RunSummary"/>.
    /// </summary>
    /// <param name="run">The run to map.</param>
    /// <returns>The mapped summary.</returns>
    public static RunSummary ToSummary(this Run run) =>
        new(run.Id, run.PipelineFileName, run.PipelineName, run.Status.ToString(), run.ExitCode, run.StartedAt, run.FinishedAt);

    /// <summary>
    /// Maps a persisted run, including its jobs and steps, to a <see cref="RunDetail"/>.
    /// </summary>
    /// <param name="run">The run to map.</param>
    /// <returns>The mapped detail.</returns>
    public static RunDetail ToDetail(this Run run) =>
        new(
            run.Id,
            run.PipelineFileName,
            run.PipelineName,
            run.Status.ToString(),
            run.ExitCode,
            run.StartedAt,
            run.FinishedAt,
            run.Jobs.Select(ToSummary).ToList());

    /// <summary>
    /// Maps an executed job, including its steps, to a <see cref="JobSummary"/>.
    /// </summary>
    /// <param name="job">The job to map.</param>
    /// <returns>The mapped summary.</returns>
    public static JobSummary ToSummary(this RunJob job) =>
        new(job.Name, job.Status.ToString(), job.ExitCode, job.Steps.Select(ToSummary).ToList());

    /// <summary>
    /// Maps an executed step to a <see cref="StepSummary"/>.
    /// </summary>
    /// <param name="step">The step to map.</param>
    /// <returns>The mapped summary.</returns>
    public static StepSummary ToSummary(this RunStep step) =>
        new(step.Name, step.Command, step.Status.ToString(), step.ExitCode, step.Output);
}
