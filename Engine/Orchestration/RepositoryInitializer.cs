using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Orchestration.Interfaces;

namespace Railway.Engine.Orchestration;

public sealed class RepositoryInitializer : IRepositoryInitializer
{
    public async Task<InitResult> InitializeAsync(string startPath, CancellationToken ct = default)
    {
        var rootRepositoryPath = RepositoryHelper.GetRootRepositoryPath(startPath);

        if (rootRepositoryPath == null)
        {
            throw new NotAGitRepositoryException();
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

        return new InitResult(Succeeded: true, RootRepositoryPath: rootRepositoryPath, Created: hasMadeChanges);
    }
}
