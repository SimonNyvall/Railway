using Railway.Engine.Domain;

namespace Railway.Engine.Services.Interfaces;

public interface IDashboardService
{
    Task<DashboardView> GetView(string startPath);
}
