using ModelContextProtocol;

namespace Railway.Mcp.Tools;

/// <summary>
/// Resolves the repository path used by the Railway MCP tools.
/// </summary>
/// <remarks>
/// The path is resolved, in order, from the explicit <c>path</c> argument, the
/// <see cref="EnvironmentVariableName"/> environment variable, and finally the process
/// current working directory. The resolved path is passed to the Engine unchanged; the
/// Engine walks upward from it to locate the <c>.railway</c> folder.
/// </remarks>
public static class RailwayPathResolver
{
    /// <summary>
    /// The name of the environment variable used as the fallback repository path.
    /// </summary>
    public const string EnvironmentVariableName = "RAILWAY_REPO_PATH";

    /// <summary>
    /// Resolves the repository path from an explicit value, the
    /// <see cref="EnvironmentVariableName"/> environment variable, or the current directory.
    /// </summary>
    /// <param name="path">An optional explicit repository path.</param>
    /// <returns>The absolute path to an existing directory.</returns>
    /// <exception cref="McpException">The resolved path does not exist or is not a directory.</exception>
    public static string Resolve(string? path) =>
        Resolve(path, Environment.GetEnvironmentVariable(EnvironmentVariableName));

    /// <summary>
    /// Resolves the repository path using an explicit environment value.
    /// </summary>
    /// <param name="path">An optional explicit repository path.</param>
    /// <param name="environmentPath">The value read from the environment, or <see langword="null" />.</param>
    /// <returns>The absolute path to an existing directory.</returns>
    /// <exception cref="McpException">The resolved path does not exist or is not a directory.</exception>
    public static string Resolve(string? path, string? environmentPath)
    {
        var candidate = FirstNonBlank(path)
            ?? FirstNonBlank(environmentPath)
            ?? Directory.GetCurrentDirectory();

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new McpException($"The repository path '{candidate}' is not a valid path: {ex.Message}");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new McpException(
                $"The repository path '{fullPath}' does not exist or is not a directory.");
        }

        return fullPath;
    }

    private static string? FirstNonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
