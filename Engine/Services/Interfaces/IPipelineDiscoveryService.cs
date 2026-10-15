using Railway.Engine.Domain;

namespace Railway.Engine.Services.Interfaces;

public interface IPipelineDiscoveryService
{
    Task<IReadOnlyList<DiscoveredPipeline>> Discover(string railwayPath);
}
