namespace Railway.Engine.Orchestration.Interfaces;

public interface IRepositoryInitializer
{
    Task<InitResult> InitializeAsync(string startPath, CancellationToken ct = default);
}
