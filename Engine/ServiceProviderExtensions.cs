using Microsoft.Extensions.DependencyInjection;

namespace Railway.Engine;

/// <summary>
/// Convenience extensions for running Engine bootstrap against a built
/// <see cref="IServiceProvider"/>.
/// </summary>
public static class ServiceProviderExtensions
{
    /// <summary>
    /// Resolves the <see cref="IEngineInitializer"/> and runs database bootstrap.
    /// Hosts should call this once at startup after building their service provider.
    /// </summary>
    /// <param name="provider">The provider built from a collection registered with <see cref="ServiceCollectionExtensions.AddEngine"/>.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that completes once initialization has finished.</returns>
    public static Task InitializeEngineAsync(this IServiceProvider provider, CancellationToken cancellationToken = default)
        => provider.GetRequiredService<IEngineInitializer>().InitializeAsync(cancellationToken);
}
