using Railway.Engine.Domain;

namespace Railway.Engine.Orchestration.Interfaces;

public interface IDashboardQuery
{
    Task<DashboardView> QueryAsync(string startPath, CancellationToken ct = default);
}
