namespace Cli.Helpers;

public static class RepositoryHelper
{
    public static string? GetRootRepositoryPath()
    {
        var currentDirectory = Directory.GetCurrentDirectory();

        return Search(currentDirectory);

        static string? Search(string? path)
        {
            if (path == null)
            {
                return null;
            }

            var directoriesInDirectory = Directory.GetDirectories(path);

            return directoriesInDirectory.Contains(Path.Combine(path, ".git"))
                ? path
                : Search(Directory.GetParent(path)?.FullName);
        }
    }
}
