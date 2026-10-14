using Cli.Handlers.Interfaces;
using Cli.Helpers;

namespace Cli.Handlers;

public class DashboardHandler : IDashboardHandler
{
    public async Task Handle()
    {
        var fullRepoPath = RepositoryHelper.GetRootRepositoryPath();

        if (fullRepoPath == null)
        {
            return;
        }

        var repoRootFolderName = new DirectoryInfo(fullRepoPath)?.Parent?.Name;
    }
}