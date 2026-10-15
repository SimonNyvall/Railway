using Railway.Engine.Domain;

namespace Railway.Engine.Orchestration.Interfaces;

public interface IPipelineLister
{
    Task<IReadOnlyList<DiscoveredPipeline>> ListAsync(string startPath, CancellationToken ct = default);
}
