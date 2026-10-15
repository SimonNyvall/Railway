using Cli.Domain;

namespace Cli.Services.Interfaces;

public interface IPipelineDiscoveryService
{
    Task<IReadOnlyList<DiscoveredPipeline>> Discover(string railwayPath);
}
