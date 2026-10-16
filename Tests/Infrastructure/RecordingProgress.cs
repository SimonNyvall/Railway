namespace Tests.Infrastructure;

/// <summary>
/// An <see cref="IProgress{T}"/> implementation that records every reported value,
/// so tests can assert on the notifications produced by production code.
/// </summary>
/// <typeparam name="T">The type of the reported progress value.</typeparam>
public sealed class RecordingProgress<T> : IProgress<T>
{
    /// <summary>Gets the reported values, in the order they were reported.</summary>
    public List<T> Values { get; } = [];

    /// <inheritdoc/>
    public void Report(T value) => Values.Add(value);
}
