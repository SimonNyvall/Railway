using Cli.Domain;
using Cli.Handlers.Interfaces;
using Cli.Helpers;
using Spectre.Console;

namespace Cli.Handlers;

public class InitHandler : IInitHandler
{
    public async Task<bool> Handle()
    {
        var rootRepositoryPath = RepositoryHelper.GetRootRepositoryPath();

        if (rootRepositoryPath == null)
        {
            AnsiConsole.WriteLine(
                "fatal: not a git repository (or any of the parent directories): .git"
            );
            return false;
        }

        var railwayPath = Path.Combine(rootRepositoryPath, RepositoryHelper.RailwayFolderName);

        var hasMadeChanges = false;

        if (!Directory.Exists(railwayPath))
        {
            Directory.CreateDirectory(railwayPath);
            hasMadeChanges = true;
        }

        var manifestPath = RepositoryHelper.GetManifestPath(railwayPath);

        if (!File.Exists(manifestPath))
        {
            var manifest = new RailwayManifest
            {
                Name = new DirectoryInfo(rootRepositoryPath).Name,
                Pipelines = []
            };
            await RailwayManifestHelper.Write(railwayPath, manifest);
            hasMadeChanges = true;
        }

        var pipelinesPath = RepositoryHelper.GetPipelinesPath(railwayPath);

        if (!Directory.Exists(pipelinesPath))
        {
            Directory.CreateDirectory(pipelinesPath);
            hasMadeChanges = true;
        }

        if (!hasMadeChanges)
        {
            AnsiConsole.WriteLine(
                $"Reinitialized existing Railway repository in {Path.Combine(rootRepositoryPath, ".git")}"
            );
            return true;
        }

        AnsiConsole.WriteLine(
            $"Initialized empty Railway repository in {Path.Combine(rootRepositoryPath, ".git")}"
        );

        return true;
    }
}
