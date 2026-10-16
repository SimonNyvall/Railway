using Railway.Engine.Domain;

namespace Railway.Engine.Orchestration.Interfaces;

/// <summary>
/// Orchestration entry point for reading persisted pipeline run history.
/// </summary>
public interface IRunHistoryQuery
{
    /// <summary>
    /// Lists the most recent pipeline runs for the repository resolved from <paramref name="startPath"/>.
    /// </summary>
    /// <param name="startPath">A path inside the target repository.</param>
    /// <param name="limit">The maximum number of runs to return. Defaults to 50.</param>
    /// <param name="ct">A token to observe while waiting for the operation to complete.</param>
    /// <returns>The latest runs, newest first; empty when the repository has no recorded runs.</returns>
    Task<IReadOnlyList<Run>> ListRunsAsync(string startPath, int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Gets a single pipeline run, including its jobs and steps, for the repository resolved from <paramref name="startPath"/>.
    /// </summary>
    /// <param name="startPath">A path inside the target repository.</param>
    /// <param name="runId">The identifier of the run to load.</param>
    /// <param name="ct">A token to observe while waiting for the operation to complete.</param>
    /// <returns>The run with its jobs and steps; step output is truncated to the last 4000 characters.</returns>
    Task<Run> GetRunAsync(string startPath, int runId, CancellationToken ct = default);
}