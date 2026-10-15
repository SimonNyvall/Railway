using Cli.Commands;
using Cli.Handlers;
using Cli.Handlers.Interfaces;
using Cli.Helpers;
using Cli.Infrastructure;
using Cli.Services;
using Cli.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace Tests.Infrastructure;

/// <summary>
/// Spectre <see cref="ITypeRegistrar"/> backed by Microsoft.Extensions.DependencyInjection.
/// The container resolves <c>IEnumerable&lt;T&gt;</c> natively (e.g.
/// <c>IEnumerable&lt;IHelpProvider&gt;</c>), which the previous hand-rolled
/// Activator-based resolver could not, causing every CommandApp.RunAsync to return -1.
/// </summary>
public class TestTypeRegistrar : ITypeRegistrar
{
    private readonly IServiceCollection _services = new ServiceCollection();

    public TestTypeRegistrar(TestConsole? console = null, string? databasePath = null)
    {
        if (console != null)
        {
            _services.AddSingleton<TestConsole>(console);
            _services.AddSingleton<IAnsiConsole>(console);
        }

        // Register all CLI services for testing with isolated database
        RegisterServices(databasePath);
    }

    private void RegisterServices(string? databasePath)
    {
        var dbPath = databasePath ?? Path.Combine(Path.GetTempPath(), "railway_test_db_" + Guid.NewGuid());
        Directory.CreateDirectory(dbPath);

        var options = new DbContextOptionsBuilder<RailwayDbContext>()
            .UseSqlite($"Data Source={dbPath}/data.db")
            .Options;

        using (var dbContext = new RailwayDbContext(options))
        {
            dbContext.Database.EnsureCreated();
        }

        // Context factory — a new context per resolve, mirroring prod LifetimeService registration
        _services.AddTransient(_ => new RailwayDbContext(options));

        // Services (mirror Cli/Program.cs LifetimeService registrations)
        _services.AddTransient<IValidateService, ValidateService>();
        _services.AddTransient<IPipelineDiscoveryService, PipelineDiscoveryService>();
        _services.AddTransient<IRunnerService, RunnerService>();
        _services.AddTransient<IDashboardService, DashboardService>();
        _services.AddTransient<IPipelineService, PipelineService>();

        // Handlers
        _services.AddTransient<IInitHandler, InitHandler>();
        _services.AddTransient<IValidationHandler, ValidationHandler>();
        _services.AddTransient<IListHandler, ListHandler>();
        _services.AddTransient<IRunHandler, RunHandler>();
        _services.AddTransient<IDashboardHandler, DashboardHandler>();

        // Command types, so Spectre can construct them through the resolver
        // (Spectre also re-registers these via RegisterDependencies before each run)
        _services.AddTransient<InitCommand>();
        _services.AddTransient<ValidateCommand>();
        _services.AddTransient<ListCommand>();
        _services.AddTransient<RunCommand>();
        _services.AddTransient<DashboardCommand>();
    }

    /// <inheritdoc/>
    public void Register(Type service, Type implementation)
    {
        _services.AddTransient(service, implementation);
    }

    /// <inheritdoc/>
    public void RegisterInstance(Type service, object implementation)
    {
        _services.AddSingleton(service, implementation);
    }

    /// <inheritdoc/>
    public void RegisterLazy(Type service, Func<object> factory)
    {
        _services.AddTransient(service, _ => factory());
    }

    /// <inheritdoc/>
    public ITypeResolver Build()
    {
        // Build a fresh provider per run so registrations made by Spectre after
        // the registrar was constructed (command/settings types, IConfiguration,
        // CommandModel, ...) are visible to the resolver for that run.
        return new TestTypeResolver(_services.BuildServiceProvider());
    }

    private class TestTypeResolver : ITypeResolver
    {
        private readonly ServiceProvider _provider;

        public TestTypeResolver(ServiceProvider provider)
        {
            _provider = provider;
        }

        /// <inheritdoc/>
        public object? Resolve(Type? type)
        {
            if (type == null) return null;

            // GetService returns null for unregistered types; Spectre's
            // TypeResolverAdapter falls back to Activator.CreateInstance then.
            // Never throw from here — an exception becomes a hard -1 exit code.
            try
            {
                return _provider.GetService(type);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
