using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Engine.Services.Interfaces;

namespace Railway.Engine.Orchestration;

public sealed class PipelineLister : IPipelineLister
{
    private readonly IPipelineDiscoveryService _pipelineDiscoveryService;

    public PipelineLister(IPipelineDiscoveryService pipelineDiscoveryService)
    {
        _pipelineDiscoveryService = pipelineDiscoveryService;
    }

    public async Task<IReadOnlyList<DiscoveredPipeline>> ListAsync(string startPath, CancellationToken ct = default)
    {
        var railwayPath = RepositoryHelper.GetRailwayPathOrThrow(startPath);

        return await _pipelineDiscoveryService.Discover(railwayPath);
    }
}
