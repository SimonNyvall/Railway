using Railway.Engine.Domain;

namespace Railway.Engine.Services.Interfaces;

/// <summary>
/// Reads persisted pipeline run history for the repository resolved from a starting path.
/// </summary>
public interface IRunHistoryService
{
    /// <summary>
    /// Lists the most recent pipeline runs for the repository resolved from <paramref name="startPath"/>.
    /// </summary>
    /// <param name="startPath">A path inside the target repository.</param>
    /// <param name="limit">The maximum number of runs to return. Defaults to 50.</param>
    /// <param name="ct">A token to observe while waiting for the operation to complete.</param>
    /// <returns>The latest runs, newest first; empty when the repository has no recorded runs.</returns>
    /// <exception cref="NotARailwayRepositoryException">No <c>.railway</c> folder or git repository was found.</exception>
    Task<IReadOnlyList<Run>> ListRunsAsync(string startPath, int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Gets a single pipeline run, including its jobs and steps, for the repository resolved from <paramref name="startPath"/>.
    /// </summary>
    /// <param name="startPath">A path inside the target repository.</param>
    /// <param name="runId">The identifier of the run to load.</param>
    /// <param name="ct">A token to observe while waiting for the operation to complete.</param>
    /// <returns>The run with its jobs and steps; step output is truncated to the last 4000 characters.</returns>
    /// <exception cref="ArgumentException">No run with <paramref name="runId"/> exists for the repository.</exception>
    /// <exception cref="NotARailwayRepositoryException">No <c>.railway</c> folder or git repository was found.</exception>
    Task<Run> GetRunAsync(string startPath, int runId, CancellationToken ct = default);
}