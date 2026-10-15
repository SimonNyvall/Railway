using Railway.Engine.Domain;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Engine.Services.Interfaces;

namespace Railway.Engine.Orchestration;

public sealed class DashboardQuery : IDashboardQuery
{
    private readonly IDashboardService _dashboardService;

    public DashboardQuery(IDashboardService dashboardService)
    {
        _dashboardService = dashboardService;
    }

    public async Task<DashboardView> QueryAsync(string startPath, CancellationToken ct = default)
    {
        return await _dashboardService.GetView(startPath);
    }
}
