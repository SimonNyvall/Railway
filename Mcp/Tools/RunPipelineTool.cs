using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Railway.Engine.Orchestration.Interfaces;

namespace Railway.Mcp.Tools;

/// <summary>
/// Exposes Railway pipeline execution as an MCP tool.
/// </summary>
[McpServerToolType]
public sealed class RunPipelineTool
{
    private readonly IPipelineRunner _pipelineRunner;
    private readonly IRunHistoryQuery _runHistoryQuery;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunPipelineTool"/> class.
    /// </summary>
    /// <param name="pipelineRunner">The runner used to execute pipelines.</param>
    /// <param name="runHistoryQuery">The query used to load the run's jobs and steps after execution.</param>
    public RunPipelineTool(IPipelineRunner pipelineRunner, IRunHistoryQuery runHistoryQuery)
    {
        _pipelineRunner = pipelineRunner;
        _runHistoryQuery = runHistoryQuery;
    }

    /// <summary>
    /// Runs a pipeline in a Railway repository and blocks until it finishes, reporting progress.
    /// </summary>
    /// <param name="file">The pipeline file name to run (for example <c>build.yaml</c>).</param>
    /// <param name="verbose"><see langword="true"/> to include the command of each step in progress updates; otherwise, <see langword="false"/>.</param>
    /// <param name="includeOutput"><see langword="true"/> to stream standard output and error lines as progress updates; otherwise, <see langword="false"/>.</param>
    /// <param name="path">An optional path inside the target repository.</param>
    /// <param name="progress">The progress sink injected by the MCP SDK, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
    /// <returns>The run identifier, pipeline name, status, exit code, and the executed jobs and steps.</returns>
    /// <exception cref="McpException">The repository or pipeline could not be resolved, or the run failed to start.</exception>
    [McpServerTool(Name = "run_pipeline", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Runs a pipeline in a Railway repository and blocks until it finishes, reporting progress notifications.")]
    public async Task<RunResult> RunAsync(
        [Description("The pipeline file name to run, for example 'build.yaml'.")] string file,
        [Description("true to include the command of each step in progress updates; otherwise, false.")] bool verbose = false,
        [Description("true to stream standard output and error lines as progress updates; otherwise, false.")] bool includeOutput = false,
        [Description("Optional path inside the target repository. Defaults to RAILWAY_REPO_PATH, then the current directory.")] string? path = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var repositoryPath = RailwayPathResolver.Resolve(path);

        return await EngineExceptionMapper.GuardAsync(async () =>
        {
            var adapter = new RunEventProgressAdapter(progress, includeOutput, verbose);
            var outcome = await _pipelineRunner.RunAsync(repositoryPath, file, verbose, adapter, cancellationToken);

            // The runner does not populate the returned run's jobs, so load the persisted
            // run to include jobs and steps in the result.
            var run = await _runHistoryQuery.GetRunAsync(repositoryPath, outcome.Run.Id, cancellationToken);

            return new RunResult(
                run.Id,
                run.PipelineName,
                run.Status.ToString(),
                run.ExitCode,
                run.Jobs.Select(job => job.ToSummary()).ToList());
        }, repositoryPath);
    }
}
