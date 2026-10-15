namespace Railway.Engine.Domain;

public sealed class RunRequest
{
    public required string RepositoryPath { get; init; }
    public required string PipelineFileName { get; init; }
    public required Pipeline Pipeline { get; init; }
    public bool Verbose { get; init; }
}
