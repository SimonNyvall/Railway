namespace Railway.Mcp.Tools;

/// <summary>
/// Describes a pipeline discovered in a repository.
/// </summary>
/// <param name="FileName">The pipeline file name.</param>
/// <param name="Path">The absolute path to the pipeline file.</param>
/// <param name="Name">The pipeline name declared in the file, if any.</param>
/// <param name="IsValid">A value that indicates whether the pipeline is valid.</param>
/// <param name="Errors">The validation errors; empty when the pipeline is valid.</param>
public sealed record PipelineSummary(
    string FileName,
    string Path,
    string? Name,
    bool IsValid,
    IReadOnlyList<string> Errors);

/// <summary>
/// Describes the outcome of validating a pipeline file.
/// </summary>
/// <param name="Name">The pipeline name declared in the file.</param>
/// <param name="Errors">The validation errors; empty when the pipeline is valid.</param>
public sealed record ValidationSummary(string Name, IReadOnlyList<string> Errors);

/// <summary>
/// Describes a single executed step.
/// </summary>
/// <param name="Name">The step name.</param>
/// <param name="Command">The command that the step runs.</param>
/// <param name="Status">The status of the step.</param>
/// <param name="ExitCode">The exit code of the step, if it completed.</param>
/// <param name="Output">The captured output of the step.</param>
public sealed record StepSummary(
    string Name,
    string Command,
    string Status,
    int? ExitCode,
    string Output);

/// <summary>
/// Describes a single executed job and its steps.
/// </summary>
/// <param name="Name">The job name.</param>
/// <param name="Status">The status of the job.</param>
/// <param name="ExitCode">The exit code of the job, if it completed.</param>
/// <param name="Steps">The steps that ran within the job.</param>
public sealed record JobSummary(
    string Name,
    string Status,
    int? ExitCode,
    IReadOnlyList<StepSummary> Steps);

/// <summary>
/// Summarizes a persisted pipeline run.
/// </summary>
/// <param name="RunId">The run identifier.</param>
/// <param name="PipelineFileName">The executed pipeline file name.</param>
/// <param name="PipelineName">The pipeline name declared in the file.</param>
/// <param name="Status">The status of the run.</param>
/// <param name="ExitCode">The overall exit code of the run, if it completed.</param>
/// <param name="StartedAt">The time at which the run started.</param>
/// <param name="FinishedAt">The time at which the run finished, if it completed.</param>
public sealed record RunSummary(
    int RunId,
    string PipelineFileName,
    string PipelineName,
    string Status,
    int? ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>
/// Provides full detail for a persisted pipeline run.
/// </summary>
/// <param name="RunId">The run identifier.</param>
/// <param name="PipelineFileName">The run's pipeline file name.</param>
/// <param name="PipelineName">The pipeline name declared in the file.</param>
/// <param name="Status">The status of the run.</param>
/// <param name="ExitCode">The exit code of the run, if it completed.</param>
/// <param name="StartedAt">The time at which the run started.</param>
/// <param name="FinishedAt">The time at which the run finished, if it completed.</param>
/// <param name="Jobs">The jobs that ran, each with its steps.</param>
public sealed record RunDetail(
    int RunId,
    string PipelineFileName,
    string PipelineName,
    string Status,
    int? ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<JobSummary> Jobs);

/// <summary>
/// Describes the outcome of running a pipeline.
/// </summary>
/// <param name="RunId">The run identifier.</param>
/// <param name="PipelineName">The pipeline name declared in the file.</param>
/// <param name="Status">The final status of the run.</param>
/// <param name="ExitCode">The exit code of the run.</param>
/// <param name="Jobs">The jobs that ran, each with its steps.</param>
public sealed record RunResult(
    int RunId,
    string PipelineName,
    string Status,
    int? ExitCode,
    IReadOnlyList<JobSummary> Jobs);

/// <summary>
/// Describes the dashboard view of a repository.
/// </summary>
/// <param name="RepositoryName">The name of the repository directory.</param>
/// <param name="Pipelines">The pipelines discovered in the repository.</param>
/// <param name="RecentRuns">The most recent runs, newest first.</param>
public sealed record DashboardResult(
    string RepositoryName,
    IReadOnlyList<PipelineSummary> Pipelines,
    IReadOnlyList<RunSummary> RecentRuns);

/// <summary>
/// Describes the result of a run-history query: either a list of runs or a single run's detail.
/// </summary>
/// <param name="Runs">The run summaries when listing history; empty when a single run was requested.</param>
/// <param name="Run">The run detail when a specific run was requested; otherwise, <see langword="null"/>.</param>
public sealed record RunsResult(IReadOnlyList<RunSummary> Runs, RunDetail? Run);
