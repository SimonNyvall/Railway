using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Railway.Engine.Orchestration.Interfaces;

namespace Railway.Mcp.Tools;

/// <summary>
/// Exposes the Railway pipeline list as an MCP tool.
/// </summary>
[McpServerToolType]
public sealed class ListPipelinesTool
{
    private readonly IPipelineLister _pipelineLister;

    /// <summary>
    /// Initializes a new instance of the <see cref="ListPipelinesTool"/> class.
    /// </summary>
    /// <param name="pipelineLister">The pipeline lister used to discover pipelines.</param>
    public ListPipelinesTool(IPipelineLister pipelineLister)
    {
        _pipelineLister = pipelineLister;
    }

    /// <summary>
    /// Lists the pipeline definitions discovered in a Railway repository.
    /// </summary>
    /// <param name="path">An optional path inside the target repository.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
    /// <returns>The discovered pipelines, each with its file name, path, name, and validity.</returns>
    /// <exception cref="McpException">The repository could not be resolved or the pipelines could not be read.</exception>
    [McpServerTool(Name = "list_pipelines", ReadOnly = true, Destructive = false)]
    [Description("Lists the pipeline definitions in a Railway repository, including whether each pipeline is valid.")]
    public async Task<IReadOnlyList<PipelineSummary>> ListAsync(
        [Description("Optional path inside the target repository. Defaults to the RAILWAY_REPO_PATH environment variable, then the current directory.")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        var repositoryPath = RailwayPathResolver.Resolve(path);

        return await EngineExceptionMapper.GuardAsync(async () =>
        {
            var pipelines = await _pipelineLister.ListAsync(repositoryPath, cancellationToken);

            return (IReadOnlyList<PipelineSummary>)pipelines.Select(p => p.ToSummary()).ToList();
        }, repositoryPath);
    }
}
