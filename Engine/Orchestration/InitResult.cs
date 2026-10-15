namespace Railway.Engine.Orchestration;

public sealed record InitResult(bool Succeeded, string? RootRepositoryPath, bool Created);
