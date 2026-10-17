using Railway.Engine.Domain;
using Railway.Engine.Services.Interfaces;

namespace Railway.Engine.Orchestration.Interfaces;

/// <summary>
/// Installs, inspects and removes a repository's pipeline triggers: generated wrapper scripts under
/// <c>.railway/hooks/</c>, git hook files with chain/backup of foreign hooks, the marker-delimited
/// <c>.gitignore</c> block, and OS scheduler entries (<c>launchd</c>/<c>crontab</c>/<c>schtasks</c>) for
/// <c>schedule</c> triggers through <see cref="Services.Interfaces.IScheduler"/>.
/// </summary>
public interface IHookInstaller
{
    /// <summary>
    /// Materializes the hook artifacts referenced by the repository's pipeline triggers; git events
    /// become hook scripts and <c>schedule</c> triggers become OS scheduler entries. Idempotent:
    /// running it twice produces the same artifacts and a single <c>.gitignore</c> block.
    /// </summary>
    /// <param name="repositoryPath">Repository root or any path inside it.</param>
    Task<InstallResult> Install(string repositoryPath);

    /// <summary>
    /// Removes only marker-owned artifacts: git hook files (restoring their
    /// <c>.railway-backup</c> originals), generated wrappers, OS scheduler entries and the
    /// <c>.gitignore</c> block.
    /// </summary>
    /// <param name="repositoryPath">Repository root or any path inside it.</param>
    /// <param name="purge">When true, also deletes the <c>.railway/logs/</c> directory.</param>
    Task<UninstallResult> Uninstall(string repositoryPath, bool purge = false);

    /// <summary>
    /// Reports, per pipeline, its declared triggers (with validation errors and computed git hook
    /// state), the lock state and the last recorded run.
    /// </summary>
    /// <param name="repositoryPath">Repository root or any path inside it.</param>
    Task<HookStatusReport> Status(string repositoryPath);

    /// <summary>
    /// Re-materializes the hook artifacts from the pipeline YAML: drops stale artifacts, adds missing
    /// ones and refreshes drifted content (including schedule entries), reporting the diff.
    /// </summary>
    /// <param name="repositoryPath">Repository root or any path inside it.</param>
    Task<SyncResult> Sync(string repositoryPath);
}

/// <summary>
/// State of a git hook file backing an event subscribed to by a pipeline.
/// </summary>
public enum GitHookState
{
    /// <summary>No hook file exists at the resolved path.</summary>
    Missing,

    /// <summary>Our hook file is present with exactly the content we would generate now.</summary>
    Installed,

    /// <summary>Our hook file is present and chains a preserved foreign hook (<c>.railway-backup</c>).</summary>
    ForeignChained,

    /// <summary>Our hook file is present but its content differs from what we would generate now.</summary>
    Drifted,

    /// <summary>A foreign hook file exists at the path and we have not installed ours yet.</summary>
    Foreign,
}

/// <summary>Kind of hook artifact an install/uninstall/sync action applied to.</summary>
public enum HookArtifactKind
{
    /// <summary>A generated wrapper script under <c>.railway/hooks/</c>.</summary>
    Wrapper,

    /// <summary>A git hook file in the resolved git hooks directory.</summary>
    GitHook,

    /// <summary>The marker-delimited <c>.gitignore</c> block.</summary>
    Gitignore,

    /// <summary>The <c>.railway/logs/</c> directory.</summary>
    Logs,

    /// <summary>A schedule entry owned by an OS scheduler (launchd plist, crontab block, schtasks task).</summary>
    Schedule,
}

/// <summary>Action applied to a hook artifact.</summary>
public enum HookArtifactAction
{
    /// <summary>The artifact was created.</summary>
    Created,

    /// <summary>An existing marker-owned artifact was rewritten.</summary>
    Updated,

    /// <summary>The artifact already matched the desired content.</summary>
    Unchanged,

    /// <summary>A stale marker-owned artifact was removed.</summary>
    Removed,

    /// <summary>A <c>.railway-backup</c> original was restored in place of our hook file.</summary>
    Restored,

    /// <summary>A foreign hook was moved to <c>.railway-backup</c> and is now chain-called.</summary>
    Chained,
}

/// <summary>One artifact touched by install/uninstall/sync.</summary>
/// <param name="Kind">What the artifact is.</param>
/// <param name="Name">Short human-readable name (e.g. <c>post-commit</c> or <c>.gitignore</c>).</param>
/// <param name="Path">Absolute path of the artifact.</param>
/// <param name="Action">Action applied to it.</param>
/// <param name="Detail">Optional extra context (e.g. the chained backup path).</param>
public sealed record HookArtifact(
    HookArtifactKind Kind,
    string Name,
    string Path,
    HookArtifactAction Action,
    string? Detail = null);

/// <summary>Outcome of <see cref="IHookInstaller.Install"/>.</summary>
/// <param name="RepositoryPath">Absolute repository root.</param>
/// <param name="Artifacts">Every artifact considered, with the action applied.</param>
/// <param name="GitignoreChanged">Whether the <c>.gitignore</c> block was added or refreshed.</param>
/// <param name="ScheduleErrors">
/// Scheduler failures, each naming the pipeline and expression; null/empty when every schedule materialized.
/// </param>
/// <param name="ScheduleWarnings">Non-fatal scheduler notes (e.g. <c>launchctl</c> unavailable).</param>
public sealed record InstallResult(
    string RepositoryPath,
    IReadOnlyList<HookArtifact> Artifacts,
    bool GitignoreChanged,
    IReadOnlyList<string>? ScheduleErrors = null,
    IReadOnlyList<string>? ScheduleWarnings = null)
{
    /// <summary>True when at least one artifact was created, updated or chained.</summary>
    public bool AnyInstalled => Artifacts.Any(artifact =>
        artifact.Action is HookArtifactAction.Created or HookArtifactAction.Updated or HookArtifactAction.Chained);
}

/// <summary>Outcome of <see cref="IHookInstaller.Uninstall"/>.</summary>
/// <param name="RepositoryPath">Absolute repository root.</param>
/// <param name="Artifacts">Every artifact considered, with the action applied.</param>
/// <param name="PurgedLogs">Whether the logs directory was deleted (<c>purge</c> was requested and it existed).</param>
/// <param name="Warnings">Non-fatal notes, e.g. a foreign hook file left in place.</param>
public sealed record UninstallResult(
    string RepositoryPath,
    IReadOnlyList<HookArtifact> Artifacts,
    bool PurgedLogs,
    IReadOnlyList<string> Warnings)
{
    /// <summary>True when at least one marker-owned artifact was removed or restored.</summary>
    public bool AnyRemoved => Artifacts.Any(artifact =>
        artifact.Action is HookArtifactAction.Removed or HookArtifactAction.Restored);
}

/// <summary>
/// Status of one declared trigger of a pipeline.
/// </summary>
/// <param name="Event">The trigger event.</param>
/// <param name="Branches">Branch filters (push).</param>
/// <param name="Tags">Tag filters (push).</param>
/// <param name="WatchPaths">Watch globs (watch).</param>
/// <param name="Cron">Cron expression (schedule).</param>
/// <param name="Errors">Validation errors collected while parsing the trigger; empty when valid.</param>
/// <param name="HookState">Git hook state for git events; null for schedule/watch.</param>
/// <param name="HookPath">Absolute path of the backing git hook file, when applicable.</param>
/// <param name="BackupPath">Absolute path of the chained foreign hook, when applicable.</param>
/// <param name="NextOccurrence">
/// Next occurrence (local time) for a valid <c>schedule</c> trigger; null for other events or an
/// unparseable expression.
/// </param>
/// <param name="ScheduleState">
/// Materialization state for a <c>schedule</c> trigger (installed/missing/drifted/unsupported); null for
/// other events or when no scheduler was available.
/// </param>
/// <param name="ScheduleEntry">Location/identity of the materialized schedule entry, when one exists.</param>
/// <param name="ScheduleError">Scheduler failure/representation error naming the pipeline and expression.</param>
public sealed record TriggerStatus(
    TriggerEvent Event,
    IReadOnlyList<string> Branches,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> WatchPaths,
    string? Cron,
    IReadOnlyList<string> Errors,
    GitHookState? HookState,
    string? HookPath,
    string? BackupPath,
    DateTimeOffset? NextOccurrence,
    ScheduleState? ScheduleState = null,
    string? ScheduleEntry = null,
    string? ScheduleError = null);

/// <summary>Summary of a repository's most recent run for one pipeline.</summary>
/// <param name="RunId">Persisted run id.</param>
/// <param name="Status">Run status (including <see cref="RunStatus.Skipped"/>).</param>
/// <param name="StartedAt">Run start timestamp.</param>
/// <param name="Trigger">Provenance key (<c>manual</c>, <c>hook</c>, <c>schedule</c>, <c>watch</c>).</param>
/// <param name="TriggerDetail">Human-readable trigger context.</param>
public sealed record RunSummary(
    int RunId,
    RunStatus Status,
    DateTimeOffset StartedAt,
    string? Trigger,
    string? TriggerDetail);

/// <summary>Status of one pipeline for hook purposes.</summary>
/// <param name="PipelineFileName">Pipeline file name (identity).</param>
/// <param name="Triggers">Declared triggers with their state.</param>
/// <param name="Errors">Pipeline-level load errors (empty when the pipeline parsed cleanly).</param>
/// <param name="LockHeld">Whether a lock file currently exists for the pipeline (cheap, may be stale).</param>
/// <param name="LastRun">Most recent recorded run, when a database was available.</param>
public sealed record PipelineHookStatus(
    string PipelineFileName,
    IReadOnlyList<TriggerStatus> Triggers,
    IReadOnlyList<string> Errors,
    bool LockHeld,
    RunSummary? LastRun);

/// <summary>Report returned by <see cref="IHookInstaller.Status"/>.</summary>
/// <param name="RepositoryPath">Absolute repository root.</param>
/// <param name="GitHooksDirectory">Resolved git hooks directory, or null when it could not be resolved.</param>
/// <param name="GitignoreConfigured">Whether the marker-delimited <c>.gitignore</c> block is present.</param>
/// <param name="Pipelines">Per-pipeline status, in discovery order.</param>
/// <param name="Scheduler">Selected scheduler name (e.g. <c>launchd</c>), or null when selection failed.</param>
public sealed record HookStatusReport(
    string RepositoryPath,
    string? GitHooksDirectory,
    bool GitignoreConfigured,
    IReadOnlyList<PipelineHookStatus> Pipelines,
    string? Scheduler = null);

/// <summary>Diff produced by <see cref="IHookInstaller.Sync"/>.</summary>
/// <param name="RepositoryPath">Absolute repository root.</param>
/// <param name="Added">Artifacts created (e.g. <c>git hook post-commit</c>).</param>
/// <param name="Removed">Stale artifacts removed (e.g. <c>wrapper pre-push.sh</c>).</param>
/// <param name="Updated">Artifacts refreshed or newly configured (e.g. <c>.gitignore block</c>).</param>
/// <param name="Errors">
/// Scheduler failures, each naming the pipeline and expression; null/empty when every schedule reached
/// the desired state.
/// </param>
public sealed record SyncResult(
    string RepositoryPath,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string>? Errors = null)
{
    /// <summary>True when the diff is empty (nothing added, removed or updated).</summary>
    public bool InSync => Added.Count == 0 && Removed.Count == 0 && Updated.Count == 0;
}
