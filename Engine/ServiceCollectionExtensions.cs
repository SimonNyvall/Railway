using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Railway.Engine.Orchestration;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Engine.Services;
using Railway.Engine.Services.Interfaces;
using Railway.Engine.Services.Schedulers;

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
        services.AddTransient<IPipelineConvertService, PipelineConvertService>();

        services.AddTransient<IRepositoryInitializer, RepositoryInitializer>();
        services.AddTransient<IPipelineValidator, PipelineValidator>();
        services.AddTransient<IPipelineLister, PipelineLister>();
        services.AddTransient<IDashboardQuery, DashboardQuery>();
        services.AddTransient<IRunHistoryQuery, RunHistoryQuery>();
        services.AddTransient<IPipelineRunner, PipelineRunner>();
        services.AddTransient<IPipelineConverter, PipelineConverter>();

        // Hook orchestration (git triggers, schedules, watcher)
        services.AddSingleton<HookPaths>();
        services.AddSingleton<IHookLauncher, EnvironmentHookLauncher>();
        // Detached children are tracked so the host can kill them when it is cancelled (CLI Ctrl+C).
        // Hosts that want to kill on cancellation share one instance across their providers.
        services.AddSingleton<ISpawnedProcessTracker, SpawnedProcessTracker>();
        services.AddTransient<ISystemCommandRunner, SystemCommandRunner>();
        services.AddTransient<INotificationService, NotificationService>();
        services.AddTransient<IHookPipelineSource, PipelineHookSource>();
        services.AddTransient<PipelineRunner>();
        services.AddTransient<IHookRunner, HookRunner>();
        services.AddTransient<IHookInstaller, HookInstaller>();

        // Phase 2 schedulers: real backends (constructible directly for tests) plus the selector that
        // picks one per platform or via RAILWAY_HOOKS_SCHEDULER.
        services.AddTransient(provider => new LaunchdScheduler(
            provider.GetRequiredService<HookPaths>(),
            provider.GetRequiredService<ISystemCommandRunner>()));
        services.AddTransient(provider => new CrontabScheduler(
            provider.GetRequiredService<HookPaths>(),
            provider.GetRequiredService<ISystemCommandRunner>()));
        services.AddTransient(provider => new SchtasksScheduler(
            provider.GetRequiredService<HookPaths>(),
            provider.GetRequiredService<ISystemCommandRunner>(),
            provider.GetRequiredService<IHookLauncher>()));
        services.AddTransient(provider => new FakeScheduler(provider.GetRequiredService<HookPaths>()));
        services.AddTransient<ISchedulerSelector, SchedulerSelector>();

        // Phase 4 watcher: the event source is the OS file watcher (transient so each session owns
        // its own FileSystemWatcher); tests override it with a fake seam.
        services.AddTransient<IWatchEventSource, FileSystemWatcherWatchEventSource>();
        services.AddTransient<IHookWatcher, HookWatcher>();

        return services;
    }
}
