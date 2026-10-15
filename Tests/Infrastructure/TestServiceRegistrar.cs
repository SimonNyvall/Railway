using Cli.Handlers;
using Cli.Handlers.Interfaces;
using Cli.Helpers;
using Cli.Infrastructure;
using Cli.Services;
using Cli.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Tests.Infrastructure;

public static class TestServiceRegistrar
{
    public static void RegisterServices(string? databasePath = null)
    {
        databasePath ??= DatabasePathHelper.Get();
        Directory.CreateDirectory(databasePath);

        var options = new DbContextOptionsBuilder<RailwayDbContext>()
            .UseSqlite($"Data Source={databasePath}/data.db")
            .Options;

        // Ensure database exists
        using (var dbContext = new RailwayDbContext(options))
        {
            dbContext.Database.EnsureCreated();
        }

        // Register factory for creating new contexts per request
        LifetimeService.Add<RailwayDbContext>(() => new RailwayDbContext(options));

        // Register CLI services
        LifetimeService.Add<IInitHandler, InitHandler>();
        LifetimeService.Add<IPipelineService, PipelineService>();
        LifetimeService.Add<IValidationHandler, ValidationHandler>();
        LifetimeService.Add<IValidateService, ValidateService>();
        LifetimeService.Add<IDashboardHandler, DashboardHandler>();
        LifetimeService.Add<IDashboardService, DashboardService>();
        LifetimeService.Add<IListHandler, ListHandler>();
        LifetimeService.Add<IPipelineDiscoveryService, PipelineDiscoveryService>();
        LifetimeService.Add<IRunHandler, RunHandler>();
        LifetimeService.Add<IRunnerService, RunnerService>();
    }
}