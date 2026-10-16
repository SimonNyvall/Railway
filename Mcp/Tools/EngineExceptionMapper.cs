using ModelContextProtocol;
using Railway.Engine.Domain;

namespace Railway.Mcp.Tools;

/// <summary>
/// Maps exceptions thrown by the Railway Engine into MCP tool errors.
/// </summary>
public static class EngineExceptionMapper
{
    /// <summary>
    /// Executes an asynchronous operation, translating any thrown exception into an <see cref="McpException"/>.
    /// </summary>
    /// <typeparam name="T">The type of the result produced by the operation.</typeparam>
    /// <param name="action">The operation to execute.</param>
    /// <param name="path">An optional repository path used to enrich error messages.</param>
    /// <returns>The result produced by the operation.</returns>
    /// <exception cref="McpException">The operation threw an exception.</exception>
    public static async Task<T> GuardAsync<T>(Func<Task<T>> action, string? path = null)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            throw Map(ex, path);
        }
    }

    /// <summary>
    /// Maps an exception to an <see cref="McpException"/> whose message is suitable for the client.
    /// </summary>
    /// <param name="exception">The exception to map.</param>
    /// <param name="path">An optional repository path used to enrich error messages.</param>
    /// <returns>The mapped MCP exception.</returns>
    public static McpException Map(Exception exception, string? path = null) => exception switch
    {
        McpException mcp => mcp,
        NotARailwayRepositoryException => new McpException(
            $"No '.railway' directory was found under '{Describe(path)}' or any parent directory. " +
            "Run 'railway init' to initialize a Railway repository."),
        NotAGitRepositoryException => new McpException(
            $"'{Describe(path)}' is not inside a git repository. Initialize a git repository or point 'path' at one."),
        PipelineFileNotFoundException ex => new McpException(
            $"Pipeline file '{ex.FileName}' was not found. Use the list_pipelines tool to see the available pipelines."),
        AmbiguousPipelineException ex => new McpException(
            $"Multiple pipeline files match '{ex.FileName}': {string.Join(", ", ex.Candidates)}. " +
            "Use a unique file name."),
        PipelineInvalidException ex => new McpException(
            $"Pipeline '{ex.FileName}' is invalid: {string.Join("; ", ex.Errors)}"),
        ArgumentException ex => new McpException(ex.Message),
        _ => new McpException("An unexpected error occurred while executing the tool."),
    };

    private static string Describe(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "the provided path" : path;
}
