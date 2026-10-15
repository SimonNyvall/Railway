namespace Railway.Engine;

/// <summary>
/// Performs one-time Engine bootstrap work (such as creating the database) that
/// cannot happen during <see cref="ServiceCollectionExtensions.AddEngine"/> because
/// that method is synchronous.
/// </summary>
public interface IEngineInitializer
{
    /// <summary>
    /// Initializes the Engine for use by creating the database path and ensuring
    /// the database schema exists.
    /// </summary>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
