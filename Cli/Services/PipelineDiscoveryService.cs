using Cli.Domain;
using Cli.Helpers;
using Cli.Services.Interfaces;

namespace Cli.Services;

public class PipelineDiscoveryService : IPipelineDiscoveryService
{
    public async Task<IReadOnlyList<DiscoveredPipeline>> Discover(string railwayPath)
    {
        return await DiscoverStatic(railwayPath);
    }

    public static async Task<IReadOnlyList<DiscoveredPipeline>> DiscoverStatic(string railwayPath)
    {
        var pipelinesPath = RepositoryHelper.GetPipelinesPath(railwayPath);
        var pipelineFiles = PipelineHelper.EnumeratePipelineFiles(pipelinesPath);

        var results = new List<DiscoveredPipeline>();

        foreach (var file in pipelineFiles)
        {
            var fileName = Path.GetRelativePath(pipelinesPath, file);
            var (pipeline, error) = await PipelineHelper.TryParse(file);

            if (error != null)
            {
                results.Add(new DiscoveredPipeline
                {
                    FileName = fileName,
                    Path = file,
                    Name = null,
                    IsValid = false,
                    Errors = [error]
                });
            }
            else
            {
                results.Add(new DiscoveredPipeline
                {
                    FileName = fileName,
                    Path = file,
                    Name = pipeline!.Name,
                    IsValid = true,
                    Errors = []
                });
            }
        }

        return results;
    }
}
