using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Railway.Engine.Orchestration;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Engine.Services;
using Railway.Engine.Services.Interfaces;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Railway.Engine;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEngine(this IServiceCollection services, EngineOptions? options = null)
    {
        var dbPath = options?.DatabasePath ?? DatabasePathHelper.Get();

        services.AddSingleton(options ?? new EngineOptions());

        services.AddSingleton<DbContextOptions<RailwayDbContext>>(_ =>
            new DbContextOptionsBuilder<RailwayDbContext>()
                .UseSqlite($"Data Source={dbPath}/data.db")
                .Options);

        services.AddTransient(sp =>
            new RailwayDbContext(sp.GetRequiredService<DbContextOptions<RailwayDbContext>>()));

        services.AddTransient<IEngineInitializer, EngineInitializer>();

        services.AddTransient<IValidateService, ValidateService>();
        services.AddTransient<IPipelineDiscoveryService, PipelineDiscoveryService>();
        services.AddTransient<IDashboardService, DashboardService>();
        services.AddTransient<IRunnerService, RunnerService>();
        services.AddTransient<IRunHistoryService, RunHistoryService>();

        services.AddTransient<IRepositoryInitializer, RepositoryInitializer>();
        services.AddTransient<IPipelineValidator, PipelineValidator>();
        services.AddTransient<IPipelineLister, PipelineLister>();
        services.AddTransient<IDashboardQuery, DashboardQuery>();
        services.AddTransient<IRunHistoryQuery, RunHistoryQuery>();
        services.AddTransient<IPipelineRunner, PipelineRunner>();

        return services;
    }
}
