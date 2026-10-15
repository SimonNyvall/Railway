namespace Cli.Domain;

public sealed class DashboardView
{
    public required string RepositoryName { get; init; }
    public IReadOnlyList<DiscoveredPipeline> Pipelines { get; init; } = [];
    public IReadOnlyList<Run> RecentRuns { get; init; } = [];
}
