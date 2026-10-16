using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Railway.Engine.Orchestration.Interfaces;

namespace Railway.Mcp.Tools;

/// <summary>
/// Exposes the Railway repository dashboard as an MCP tool.
/// </summary>
[McpServerToolType]
public sealed class DashboardTool
{
    private readonly IDashboardQuery _dashboardQuery;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardTool"/> class.
    /// </summary>
    /// <param name="dashboardQuery">The query used to load the dashboard view.</param>
    public DashboardTool(IDashboardQuery dashboardQuery)
    {
        _dashboardQuery = dashboardQuery;
    }

    /// <summary>
    /// Gets the dashboard view for a Railway repository.
    /// </summary>
    /// <param name="path">An optional path inside the target repository.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
    /// <returns>The repository name, its pipelines, and its most recent runs.</returns>
    /// <exception cref="McpException">The repository could not be resolved or the dashboard could not be read.</exception>
    [McpServerTool(Name = "dashboard", ReadOnly = true, Destructive = false)]
    [Description("Shows a Railway repository's name, pipelines, and most recent runs.")]
    public async Task<DashboardResult> QueryAsync(
        [Description("Optional path inside the target repository. Defaults to RAILWAY_REPO_PATH, then the current directory.")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        var repositoryPath = RailwayPathResolver.Resolve(path);

        return await EngineExceptionMapper.GuardAsync(async () =>
        {
            var view = await _dashboardQuery.QueryAsync(repositoryPath, cancellationToken);
            return new DashboardResult(
                view.RepositoryName,
                view.Pipelines.Select(pipeline => pipeline.ToSummary()).ToList(),
                view.RecentRuns.Select(run => run.ToSummary()).ToList());
        }, repositoryPath);
    }
}
