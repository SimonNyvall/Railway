using System.Text;

using Railway.Engine.Domain;

namespace Railway.Engine.Orchestration.Interfaces;

/// <summary>
/// Runs the pipelines subscribed to a hook event (git trigger, schedule, watcher), recording run
/// provenance and skipping (never throwing) when a pipeline is already running.
/// </summary>
public interface IHookRunner
{
    /// <summary>
    /// Fires <paramref name="triggerEvent"/> for every pipeline in the repository that subscribes to it.
    /// </summary>
    /// <param name="repositoryPath">Repository root or any path inside it (never the current directory).</param>
    /// <param name="triggerEvent">The event that fired.</param>
    /// <param name="context">Per-event context used for branch/tag filtering and provenance.</param>
    /// <param name="pipelineFile">
    /// Optional pipeline file name restricting the fan-out to a single pipeline (matched by file name,
    /// like <c>PipelineRunner</c>).
    /// </param>
    /// <param name="detach">
    /// When true, spawns a detached child process per pipeline (calling <c>hooks run … --no-detach</c>)
    /// and returns immediately with synthetic "detached" results instead of running in this process.
    /// </param>
    /// <param name="notify">When false, no notifications are sent regardless of pipeline preferences.</param>
    /// <param name="ct">Cancellation token observed while running pipelines.</param>
    /// <returns>
    /// One result per selected pipeline. Pipeline-level failures become <see cref="HookRunResult.Error"/>
    /// results — this method only throws domain exceptions for repository-resolution problems
    /// (e.g. <see cref="NotARailwayRepositoryException"/>).
    /// </returns>
    Task<HookRunOutcome> RunEventAsync(
        string repositoryPath,
        TriggerEvent triggerEvent,
        HookEventContext context,
        string? pipelineFile,
        bool detach,
        bool notify,
        CancellationToken ct);
}

/// <summary>
/// Per-event context captured by the host from the provoking git operation, used for branch/tag
/// filtering and to build the human-readable <c>Run.TriggerDetail</c> provenance string.
/// </summary>
/// <param name="Branch">Branch name for branch pushes/checkouts (e.g. <c>main</c>).</param>
/// <param name="Sha">Commit SHA involved in the event.</param>
/// <param name="Tag">Tag name for tag pushes (e.g. <c>v1.2</c>).</param>
/// <param name="Remote">Remote name for pushes (e.g. <c>origin</c>).</param>
/// <param name="IsSquash">Whether a merge was a squash merge (<c>post-merge</c>).</param>
/// <param name="PreviousSha">Previous HEAD for checkouts/merges.</param>
/// <param name="NewSha">New HEAD for checkouts/merges.</param>
/// <param name="Flag">Raw git flag argument (e.g. <c>1</c> for a branch checkout).</param>
/// <param name="Note">Free-form extra detail supplied by the host.</param>
/// <param name="Cron">Cron expression of the schedule entry that fired (<c>schedule</c> events).</param>
public sealed record HookEventContext(
    string? Branch = null,
    string? Sha = null,
    string? Tag = null,
    string? Remote = null,
    bool? IsSquash = null,
    string? PreviousSha = null,
    string? NewSha = null,
    string? Flag = null,
    string? Note = null,
    string? Cron = null)
{
    /// <summary>Maximum length of <c>Run.TriggerDetail</c> (the persisted column is 200 characters).</summary>
    public const int MaxDetailLength = 200;

    /// <summary>
    /// Builds the provenance detail string (e.g. <c>branch=main, sha=abc1234</c>), or null when the
    /// context carries nothing. Truncated to <see cref="MaxDetailLength"/>.
    /// </summary>
    public string? GetDetail()
    {
        var builder = new StringBuilder();

        void Append(string key, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            builder.Append(key).Append('=').Append(value);
        }

        Append("branch", Branch);
        Append("sha", Sha);
        Append("tag", Tag);
        Append("remote", Remote);
        Append("is_squash", IsSquash?.ToString().ToLowerInvariant());
        Append("prev", PreviousSha);
        Append("new", NewSha);
        Append("flag", Flag);
        Append("cron", Cron);
        Append("note", Note);

        if (builder.Length == 0)
        {
            return null;
        }

        var detail = builder.ToString();
        return detail.Length <= MaxDetailLength ? detail : detail[..MaxDetailLength];
    }
}

/// <summary>
/// Result of one pipeline considered by <see cref="IHookRunner.RunEventAsync"/>.
/// </summary>
/// <param name="PipelineFileName">Pipeline file name that was selected.</param>
/// <param name="RunId">Persisted run id, or null when no run row exists (detached spawn, parse failure).</param>
/// <param name="Status">Run status, or <see cref="RunStatus.Failed"/> for a pipeline-level error.</param>
/// <param name="Skipped">True when the run was skipped because the pipeline lock was held.</param>
/// <param name="Error">Error message for pipeline-level failures; null otherwise.</param>
/// <param name="Detached">True when a detached child was spawned instead of running in this process.</param>
public sealed record HookRunResult(
    string PipelineFileName,
    int? RunId,
    RunStatus Status,
    bool Skipped,
    string? Error,
    bool Detached = false);

/// <summary>
/// Aggregate outcome of firing one hook event across all subscribed pipelines.
/// </summary>
/// <param name="Results">Per-pipeline results, in pipeline discovery order.</param>
public sealed record HookRunOutcome(IReadOnlyList<HookRunResult> Results)
{
    /// <summary>True when at least one pipeline failed (including pipeline-level errors).</summary>
    public bool AnyFailed => Results.Any(result => result.Status == RunStatus.Failed);

    /// <summary>True when at least one pipeline was skipped because its lock was held.</summary>
    public bool AnySkipped => Results.Any(result => result.Skipped);
}

/// <summary>
/// Candidate pipeline for a hook event: the discovered file plus its parsed pipeline (null when the
/// file could not be parsed) and the errors collected while loading it.
/// </summary>
/// <param name="FileName">Pipeline file name (last path component), the pipeline identity.</param>
/// <param name="Path">Absolute path of the pipeline file.</param>
/// <param name="Pipeline">The parsed pipeline, or null when parsing failed.</param>
/// <param name="Errors">Parse/load errors; empty when the pipeline parsed cleanly.</param>
public sealed record HookPipelineCandidate(
    string FileName,
    string Path,
    Pipeline? Pipeline,
    IReadOnlyList<string> Errors);

/// <summary>
/// Seam for loading the pipelines of a repository for hook purposes (discovery order preserved).
/// The production implementation wraps <c>IPipelineDiscoveryService</c> plus <c>PipelineHelper</c>;
/// tests supply in-memory candidates with pre-built <see cref="TriggerDefinition"/>s.
/// </summary>
public interface IHookPipelineSource
{
    /// <summary>Loads the candidate pipelines of a repository.</summary>
    /// <param name="railwayPath">Absolute path of the repository's <c>.railway</c> directory.</param>
    Task<IReadOnlyList<HookPipelineCandidate>> GetPipelinesAsync(string railwayPath);
}
