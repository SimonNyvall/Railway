using Cli.Commands;
using Cli.Handlers;
using Cli.Handlers.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Railway.Engine;
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
    // Stands in for the single shared database the old static service locator
    // (registered once by the collection fixture) gave every integration test.
    // Registrars created without an explicit path therefore resolve the same DB,
    // so a run written by one registrar is visible to the next.
    private static readonly string SharedDatabasePath =
        Path.Combine(Path.GetTempPath(), "railway_test_db_" + Guid.NewGuid());

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
        var dbPath = databasePath ?? SharedDatabasePath;

        // Engine services + DbContext with an isolated database
        _services.AddEngine(new EngineOptions { DatabasePath = dbPath });

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

        // Engine owns EnsureCreated; create the database before the tests use it.
        using var provider = _services.BuildServiceProvider();
        provider.InitializeEngineAsync().GetAwaiter().GetResult();
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
