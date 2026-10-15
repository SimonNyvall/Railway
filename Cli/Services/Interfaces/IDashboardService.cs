using Cli.Domain;

namespace Cli.Services.Interfaces;

public interface IDashboardService
{
    Task<DashboardView> GetView();
}
