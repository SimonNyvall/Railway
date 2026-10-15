using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace Cli.Infrastructure;

/// <summary>
/// Spectre <see cref="ITypeRegistrar"/> backed by Microsoft.Extensions.DependencyInjection.
/// The container resolves <c>IEnumerable&lt;T&gt;</c> natively (e.g.
/// <c>IEnumerable&lt;IHelpProvider&gt;</c>), which the previous hand-rolled
/// Activator-based resolver could not, causing every CommandApp.RunAsync to return -1.
/// </summary>
public sealed class MsDiTypeRegistrar : ITypeRegistrar
{
    private readonly IServiceCollection _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="MsDiTypeRegistrar"/> class.
    /// </summary>
    /// <param name="services">The service collection that Spectre registrations are added to.</param>
    public MsDiTypeRegistrar(IServiceCollection services)
    {
        _services = services;
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
        return new MsDiTypeResolver(_services.BuildServiceProvider());
    }

    private sealed class MsDiTypeResolver : ITypeResolver
    {
        private readonly ServiceProvider _provider;

        public MsDiTypeResolver(ServiceProvider provider)
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
