using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Railway.Engine.Orchestration.Interfaces;

namespace Railway.Mcp.Tools;

/// <summary>
/// Exposes Railway run history as an MCP tool.
/// </summary>
[McpServerToolType]
public sealed class RunsTool
{
    private readonly IRunHistoryQuery _runHistoryQuery;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunsTool"/> class.
    /// </summary>
    /// <param name="runHistoryQuery">The query used to read persisted runs.</param>
    public RunsTool(IRunHistoryQuery runHistoryQuery)
    {
        _runHistoryQuery = runHistoryQuery;
    }

    /// <summary>
    /// Lists recent pipeline runs, or returns full detail for a single run.
    /// </summary>
    /// <param name="limit">The maximum number of runs to return when listing history.</param>
    /// <param name="runId">An optional run identifier; when supplied, full detail for that run is returned.</param>
    /// <param name="path">An optional path inside the target repository.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
    /// <returns>A list of run summaries, or the detail of the requested run.</returns>
    /// <exception cref="McpException">The repository could not be resolved or the run history could not be read.</exception>
    [McpServerTool(Name = "runs", ReadOnly = true, Destructive = false)]
    [Description("Lists recent pipeline runs for a Railway repository, or returns detail (jobs and steps) for a single run.")]
    public async Task<RunsResult> QueryAsync(
        [Description("The maximum number of runs to return when listing history. Defaults to 50.")] int limit = 50,
        [Description("An optional run id. When provided, full detail for that run is returned instead of a list.")] int? runId = null,
        [Description("Optional path inside the target repository. Defaults to RAILWAY_REPO_PATH, then the current directory.")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        var repositoryPath = RailwayPathResolver.Resolve(path);

        return await EngineExceptionMapper.GuardAsync(async () =>
        {
            if (runId is int id)
            {
                var run = await _runHistoryQuery.GetRunAsync(repositoryPath, id, cancellationToken);
                return new RunsResult(Array.Empty<RunSummary>(), run.ToDetail());
            }

            var runs = await _runHistoryQuery.ListRunsAsync(repositoryPath, limit, cancellationToken);
            return new RunsResult(runs.Select(run => run.ToSummary()).ToList(), null);
        }, repositoryPath);
    }
}
