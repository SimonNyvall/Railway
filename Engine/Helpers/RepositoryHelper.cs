using Railway.Engine.Domain;

namespace Railway.Engine.Helpers;

public static class RepositoryHelper
{
    public const string RailwayFolderName = ".railway";
    public const string PipelinesFolderName = "pipelines";
    public const string ManifestFileName = "railway.yaml";

    public static string? GetRootRepositoryPath(string startPath) =>
        FindUpwards(startPath, path => Directory.Exists(Path.Combine(path, ".git")));

    public static string? GetRailwayPath(string startPath)
    {
        var rootPath = FindUpwards(startPath, path =>
            Directory.Exists(Path.Combine(path, RailwayFolderName))
        );

        return rootPath == null ? null : Path.Combine(rootPath, RailwayFolderName);
    }

    public static string GetRailwayPathOrThrow(string startPath) =>
        GetRailwayPath(startPath) ?? throw new NotARailwayRepositoryException();

    public static string GetPipelinesPath(string railwayPath) =>
        Path.Combine(railwayPath, PipelinesFolderName);

    public static string GetManifestPath(string railwayPath) =>
        Path.Combine(railwayPath, ManifestFileName);

    public static string? FindUpwards(string startPath, Func<string, bool> predicate)
    {
        var current = Path.GetFullPath(startPath);

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
