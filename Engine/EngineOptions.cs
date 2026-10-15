namespace Railway.Engine;

public sealed class EngineOptions
{
    public string? DatabasePath { get; init; }
    public bool InitializeDatabase { get; init; } = true;
}
