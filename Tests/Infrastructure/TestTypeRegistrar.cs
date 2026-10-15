using Cli.Handlers;
using Cli.Handlers.Interfaces;
using Cli.Helpers;
using Cli.Infrastructure;
using Cli.Services;
using Cli.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace Tests.Infrastructure;

public class TestTypeRegistrar : ITypeRegistrar
{
    private readonly Dictionary<Type, Func<object>> _factories = [];
    private readonly Dictionary<Type, Type> _typeMap = [];

    public TestTypeRegistrar(TestConsole? console = null, string? databasePath = null)
    {
        if (console != null)
        {
            RegisterInstance(typeof(TestConsole), console);
            var iconsoleType = Type.GetType("Spectre.Console.IConsole, Spectre.Console");
            if (iconsoleType != null)
            {
                RegisterInstance(iconsoleType, console);
            }
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

        var dbContext = new RailwayDbContext(options);
        dbContext.Database.EnsureCreated();

        // Register core services
        var validateService = new ValidateService();
        var pipelineDiscoveryService = new PipelineDiscoveryService();
        var runnerService = new RunnerService(dbContext);
        var dashboardService = new DashboardService(dbContext);
        var pipelineService = new PipelineService(dbContext);

        // Register services as instances (using interface-accepting constructors)
        RegisterInstance(typeof(RailwayDbContext), dbContext);
        RegisterInstance(typeof(IValidateService), validateService);
        RegisterInstance(typeof(IPipelineDiscoveryService), pipelineDiscoveryService);
        RegisterInstance(typeof(IRunnerService), runnerService);
        RegisterInstance(typeof(IDashboardService), dashboardService);
        RegisterInstance(typeof(IPipelineService), pipelineService);

        // Register handlers with their dependencies
        RegisterInstance(typeof(IInitHandler), new InitHandler());
        RegisterInstance(typeof(IValidationHandler), new ValidationHandler(validateService));
        RegisterInstance(typeof(IListHandler), new ListHandler(pipelineDiscoveryService));
        RegisterInstance(typeof(IRunHandler), new RunHandler(runnerService, pipelineDiscoveryService, validateService));
        RegisterInstance(typeof(IDashboardHandler), new DashboardHandler(dashboardService));

        // Register Spectre.Console.Cli required services
        var helpProviderType = Type.GetType("Spectre.Console.Cli.Help.IHelpProvider, Spectre.Console.Cli");
        if (helpProviderType != null)
        {
            var defaultHelpProviderType = Type.GetType("Spectre.Console.Cli.Help.DefaultHelpProvider, Spectre.Console.Cli");
            if (defaultHelpProviderType != null)
            {
                Register(helpProviderType, defaultHelpProviderType);
            }
        }

        var typeResolverType = Type.GetType("Spectre.Console.Cli.ITypeResolver, Spectre.Console.Cli");
        var defaultTypeResolverType = Type.GetType("Spectre.Console.Cli.DefaultTypeResolver, Spectre.Console.Cli");
        if (typeResolverType != null && defaultTypeResolverType != null)
        {
            Register(typeResolverType, defaultTypeResolverType);
        }
    }

    public void Register(Type service, Type implementation)
    {
        _typeMap[service] = implementation;
    }

    public void RegisterInstance(Type service, object implementation)
    {
        _factories[service] = () => implementation;
    }

    public void RegisterLazy(Type service, Func<object> factory)
    {
        _factories[service] = factory;
    }

    public ITypeResolver Build()
    {
        return new TestTypeResolver(_factories, _typeMap);
    }

    private class TestTypeResolver : ITypeResolver
    {
        private readonly Dictionary<Type, Func<object>> _factories;
        private readonly Dictionary<Type, Type> _typeMap;

        public TestTypeResolver(Dictionary<Type, Func<object>> factories, Dictionary<Type, Type> typeMap)
        {
            _factories = factories;
            _typeMap = typeMap;
        }

        public object? Resolve(Type? type)
        {
            if (type == null) return null;

            if (_factories.TryGetValue(type, out var factory))
            {
                return factory();
            }

            if (_typeMap.TryGetValue(type, out var implementationType))
            {
                return Activator.CreateInstance(implementationType);
            }

            return Activator.CreateInstance(type);
        }

        public T Resolve<T>() => (T)Resolve(typeof(T))!;
    }
}
