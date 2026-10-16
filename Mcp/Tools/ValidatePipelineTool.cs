using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Orchestration.Interfaces;

namespace Railway.Mcp.Tools;

/// <summary>
/// Exposes Railway pipeline validation as an MCP tool.
/// </summary>
[McpServerToolType]
public sealed class ValidatePipelineTool
{
    private readonly IPipelineValidator _pipelineValidator;

    /// <summary>
    /// Initializes a new instance of the <see cref="ValidatePipelineTool"/> class.
    /// </summary>
    /// <param name="pipelineValidator">The validator used to validate pipeline files.</param>
    public ValidatePipelineTool(IPipelineValidator pipelineValidator)
    {
        _pipelineValidator = pipelineValidator;
    }

    /// <summary>
    /// Validates a pipeline definition file in a Railway repository.
    /// </summary>
    /// <param name="file">The pipeline file to validate; absolute, relative to <c>.railway/pipelines</c>, or relative to the repository root.</param>
    /// <param name="path">An optional path inside the target repository.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
    /// <returns>The pipeline name and its validation errors; an empty error list means the pipeline is valid.</returns>
    /// <exception cref="McpException">The repository or file could not be resolved, or the file is not a valid pipeline.</exception>
    [McpServerTool(Name = "validate_pipeline", ReadOnly = true, Destructive = false)]
    [Description("Validates a pipeline definition file in a Railway repository and returns any validation errors.")]
    public async Task<ValidationSummary> ValidateAsync(
        [Description("The pipeline file to validate. May be absolute, relative to '.railway/pipelines', or relative to the repository root.")] string file,
        [Description("Optional path inside the target repository. Defaults to RAILWAY_REPO_PATH, then the current directory.")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        var repositoryPath = RailwayPathResolver.Resolve(path);

        return await EngineExceptionMapper.GuardAsync(async () =>
        {
            var filePath = ResolvePipelineFile(file, repositoryPath);
            var result = await _pipelineValidator.ValidateAsync(filePath, cancellationToken);
            return new ValidationSummary(result.Pipeline.Name, result.Errors);
        }, repositoryPath);
    }

    /// <summary>
    /// Resolves the pipeline file from an absolute path, the pipelines directory, or the repository root.
    /// </summary>
    private static string ResolvePipelineFile(string file, string repositoryPath)
    {
        if (Path.IsPathRooted(file))
        {
            if (!File.Exists(file))
            {
                throw new PipelineFileNotFoundException(file);
            }

            return file;
        }

        var railwayPath = RepositoryHelper.GetRailwayPath(repositoryPath);
        if (railwayPath is not null)
        {
            var candidate = Path.Combine(railwayPath, RepositoryHelper.PipelinesFolderName, file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var rootPath = RepositoryHelper.GetRootRepositoryPath(repositoryPath);
        if (rootPath is not null)
        {
            var candidate = Path.Combine(rootPath, file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (railwayPath is null)
        {
            throw new NotARailwayRepositoryException();
        }

        throw new PipelineFileNotFoundException(file);
    }
}
