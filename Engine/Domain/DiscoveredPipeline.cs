namespace Railway.Engine.Domain;

public sealed class DiscoveredPipeline
{
    public required string FileName { get; init; }
    public required string Path { get; init; }
    public string? Name { get; init; }
    public bool IsValid { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
}
