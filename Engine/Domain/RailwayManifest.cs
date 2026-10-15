namespace Railway.Engine.Domain;

public class RailwayManifest
{
    public string? Name { get; set; }
    public List<ManifestPipeline> Pipelines { get; set; } = [];
}

public class ManifestPipeline
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}
