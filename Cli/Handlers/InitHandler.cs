using Cli.Handlers.Interfaces;
using Cli.Helpers;
using Spectre.Console;

namespace Cli.Handlers;

public class InitHandler : IInitHandler
{
    public async Task Handle()
    {
        var rootRepositorPath = RepositoryHelper.GetRootRepositoryPath();

        if (rootRepositorPath == null)
        {
            AnsiConsole.WriteLine(
                "fatal: not a git repository (or any of the parent directories): .git"
            );
            return;
        }

        var railwayPath = Path.Combine(rootRepositorPath, ".railway");

        var hasMadeChanges = false;

        if (!Directory.Exists(railwayPath))
        {
            Directory.CreateDirectory(railwayPath);
            hasMadeChanges = true;
        }

        var railwayYamlPath = Path.Combine(railwayPath, "railway.yaml");

        if (!File.Exists(railwayYamlPath))
        {
            File.Create(railwayYamlPath);
            hasMadeChanges = true;
        }

        var pipelinePath = Path.Combine(railwayPath, "pipelines");

        if (!Directory.Exists(pipelinePath))
        {
            Directory.CreateDirectory(pipelinePath);
            hasMadeChanges = true;
        }

        if (!hasMadeChanges)
        {
            AnsiConsole.WriteLine(
                $"Reinitialized existing Railway repository in {Path.Combine(rootRepositorPath, ".git")}"
            );
            return;
        }

        AnsiConsole.WriteLine(
            $"Initialized empty Railway repository in {Path.Combine(rootRepositorPath, ".git")}"
        );
    }
}
