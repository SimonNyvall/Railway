using Cli.Domain;

namespace Cli.Helpers;

public static class RepositoryHelper
{
    public const string RailwayFolderName = ".railway";
    public const string PipelinesFolderName = "pipelines";
    public const string ManifestFileName = "railway.yaml";

    public static string? GetRootRepositoryPath() =>
        FindUpwards(path => Directory.Exists(Path.Combine(path, ".git")));

    public static string? GetRailwayPath()
    {
        var rootPath = FindUpwards(path =>
            Directory.Exists(Path.Combine(path, RailwayFolderName))
        );

        return rootPath == null ? null : Path.Combine(rootPath, RailwayFolderName);
    }

    public static string GetRailwayPathOrThrow() =>
        GetRailwayPath() ?? throw new NotARailwayRepositoryException();

    public static string GetPipelinesPath(string railwayPath) =>
        Path.Combine(railwayPath, PipelinesFolderName);

    public static string GetManifestPath(string railwayPath) =>
        Path.Combine(railwayPath, ManifestFileName);

    private static string? FindUpwards(Func<string, bool> predicate)
    {
        var current = Directory.GetCurrentDirectory();

        while (current != null)
        {
            if (predicate(current))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }
}
