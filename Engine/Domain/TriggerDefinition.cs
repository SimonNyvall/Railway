namespace Railway.Engine.Domain;

/// <summary>
/// A single parsed <c>triggers:</c> entry of a pipeline: the event plus its options
/// (branch/tag filters for <c>push</c>, cron expression for <c>schedule</c>, globs for
/// <c>watch</c>) and any validation <see cref="Errors"/> collected while parsing.
/// </summary>
public sealed class TriggerDefinition
{
    /// <summary>Gets or sets the trigger event this entry subscribes to.</summary>
    public TriggerEvent Event { get; set; }

    /// <summary>Gets or sets the branch filters (wildcards allowed).</summary>
    /// <value>An empty list (the default) matches all branches.</value>
    public IReadOnlyList<string> Branches { get; set; } = [];

    /// <summary>Gets or sets the tag filters (wildcards allowed).</summary>
    /// <value>An empty list (the default) matches all tags.</value>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>Gets or sets the watch globs; only meaningful for <see cref="TriggerEvent.Watch"/>.</summary>
    /// <value>An empty list (the default) when the trigger is not a watch trigger.</value>
    public IReadOnlyList<string> WatchPaths { get; set; } = [];

    /// <summary>Gets or sets the cron expression; only meaningful for <see cref="TriggerEvent.Schedule"/>.</summary>
    /// <value><see langword="null"/> (the default) when the trigger is not a schedule trigger.</value>
    public string? Cron { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a <see cref="TriggerEvent.PostCheckout"/> trigger also fires
    /// for the initial clone checkout.
    /// </summary>
    /// <value>
    /// <see langword="false"/> (the default): git's <c>post-checkout</c> clone checkout (previous HEAD all zeros)
    /// is ignored. Only meaningful for <see cref="TriggerEvent.PostCheckout"/>; configured as
    /// <c>- post-checkout: { clone: true }</c>.
    /// </value>
    public bool IncludeClone { get; set; }

    /// <summary>Gets or sets the validation problems found for this entry.</summary>
    /// <value>An empty list (the default) when the entry is valid.</value>
    /// <remarks>
    /// Backed by a mutable list so the YAML converter can append diagnostics while parsing
    /// (see <see cref="AddError"/>); the setter keeps object-initializer construction possible.
    /// </remarks>
    public IReadOnlyList<string> Errors
    {
        get => _errors;
        set => _errors = [.. value];
    }

    private List<string> _errors = [];

    /// <summary>Appends a validation diagnostic to <see cref="Errors"/>.</summary>
    /// <param name="error">The human-readable problem description.</param>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="error"/> is empty or whitespace.</exception>
    public void AddError(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        _errors.Add(error);
    }
}
