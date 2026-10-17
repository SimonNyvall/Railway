namespace Railway.Engine.Services.Interfaces;

/// <summary>
/// Host-agnostic seam for materializing <c>schedule</c> triggers on the operating system's scheduler
/// (launchd on macOS, crontab on Linux, schtasks on Windows, plus an in-memory <c>fake</c> used by
/// tests). Implementations only report; they never render and never write to stdout.
/// </summary>
/// <remarks>
/// All machine interaction goes through <see cref="Infrastructure.ISystemCommandRunner"/> and all paths
/// come from <see cref="Helpers.HookPaths"/> (never the current working directory). A scheduler must
/// never silently drop a requested schedule: an expression it cannot represent becomes a
/// <see cref="ScheduleAction.Failed"/> outcome (or <see cref="ScheduleState.Unsupported"/> in
/// <see cref="Inspect"/>) carrying a message that names the pipeline and the expression.
/// </remarks>
public interface IScheduler
{
    /// <summary>Gets the short identifier of the scheduler (<c>launchd</c>, <c>crontab</c>, <c>schtasks</c>, <c>fake</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Gets a value that indicates whether materialized entries invoke the generated per-pipeline wrapper
    /// script (<c>.railway/hooks/schedule-&lt;slug&gt;.sh</c>) instead of the launcher directly. POSIX schedulers
    /// use the wrapper (for a baked <c>PATH</c> and log redirection); schtasks and the fake do not.
    /// </summary>
    bool UsesWrapperScript { get; }

    /// <summary>Gets a value that indicates whether this scheduler can operate on the current machine (best-effort, never throws).</summary>
    /// <returns><see langword="true" /> if the scheduler is usable here; otherwise, <see langword="false" />.</returns>
    bool IsSupported();

    /// <summary>Lists the scheduler-owned entries currently materialized for a repository.</summary>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    /// <returns>One entry per (pipeline, cron) owned by railway; empty when none or unreachable.</returns>
    IReadOnlyList<ScheduleListEntry> List(string repositoryPath);

    /// <summary>Creates or refreshes one (pipeline, cron) schedule.</summary>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    /// <param name="request">The pipeline file name and its cron expression.</param>
    /// <returns>The outcome; <see cref="ScheduleAction.Failed"/> when the expression cannot be represented.</returns>
    ScheduleInstallOutcome Install(string repositoryPath, ScheduleRequest request);

    /// <summary>Removes one (pipeline, cron) schedule, touching only marker-owned entries.</summary>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    /// <param name="pipelineFileName">Pipeline file name, e.g. <c>build.yaml</c>.</param>
    /// <param name="cronExpression">The cron expression of the entry to remove.</param>
    /// <returns>The outcome; <see cref="ScheduleAction.Failed"/> when the entry exists but is not owned by railway.</returns>
    ScheduleRemoveOutcome Remove(string repositoryPath, string pipelineFileName, string cronExpression);

    /// <summary>
    /// Reconciles the whole repository: installs every desired (pipeline, cron), then removes owned
    /// entries that are no longer desired. Used by <c>hooks sync</c> and <c>hooks uninstall</c>.
    /// </summary>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    /// <param name="desired">The full desired schedule set (empty removes every owned entry).</param>
    /// <returns>One outcome per entry touched, in install-then-remove order.</returns>
    ScheduleReconcileResult Reconcile(string repositoryPath, IReadOnlyList<ScheduleRequest> desired);

    /// <summary>Inspects one (pipeline, cron) schedule without modifying the machine.</summary>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    /// <param name="request">The pipeline file name and its cron expression.</param>
    /// <returns>The materialization state, the entry identity when one exists, and any representation error.</returns>
    ScheduleStatusEntry Inspect(string repositoryPath, ScheduleRequest request);
}

/// <summary>A desired schedule entry: one cron expression of one pipeline.</summary>
/// <param name="PipelineFileName">Pipeline file name (identity), e.g. <c>build.yaml</c>.</param>
/// <param name="Cron">The cron expression (<c>minutes hours day-of-month month day-of-week</c>).</param>
public sealed record ScheduleRequest(string PipelineFileName, string Cron);

/// <summary>A scheduler-owned entry discovered on the machine.</summary>
/// <param name="PipelineFileName">Pipeline file name the entry was materialized for.</param>
/// <param name="Cron">The cron expression the entry represents.</param>
/// <param name="EntryName">Scheduler-specific location/identity (plist path, task name, block/crontab label).</param>
public sealed record ScheduleListEntry(string PipelineFileName, string Cron, string EntryName);

/// <summary>Action applied to a schedule entry.</summary>
public enum ScheduleAction
{
    /// <summary>The entry did not exist and was created.</summary>
    Created,

    /// <summary>An existing owned entry was refreshed.</summary>
    Updated,

    /// <summary>The entry already matched the desired state.</summary>
    Unchanged,

    /// <summary>An owned entry was removed.</summary>
    Removed,

    /// <summary>The operation failed; <see cref="ScheduleInstallOutcome.Error"/> explains why.</summary>
    Failed,
}

/// <summary>State of a declared (pipeline, cron) schedule for <c>hooks status</c>.</summary>
public enum ScheduleState
{
    /// <summary>The schedule is materialized exactly as declared.</summary>
    Installed,

    /// <summary>No entry exists for the pipeline.</summary>
    Missing,

    /// <summary>An entry exists for the pipeline but not for this expression (e.g. the cron was edited).</summary>
    Drifted,

    /// <summary>The expression cannot be represented by this scheduler.</summary>
    Unsupported,
}

/// <summary>Outcome of installing one (pipeline, cron) schedule.</summary>
/// <param name="PipelineFileName">Pipeline file name.</param>
/// <param name="Cron">The cron expression.</param>
/// <param name="EntryName">Scheduler-specific location/identity of the entry.</param>
/// <param name="Action">What happened.</param>
/// <param name="Error">Failure reason naming the pipeline and expression; null unless <see cref="Action"/> is failed.</param>
/// <param name="Warning">Non-fatal note (e.g. <c>launchctl</c> unavailable); null when there is none.</param>
public sealed record ScheduleInstallOutcome(
    string PipelineFileName,
    string Cron,
    string EntryName,
    ScheduleAction Action,
    string? Error = null,
    string? Warning = null);

/// <summary>Outcome of removing one (pipeline, cron) schedule.</summary>
/// <param name="PipelineFileName">Pipeline file name.</param>
/// <param name="Cron">The cron expression.</param>
/// <param name="EntryName">Scheduler-specific location/identity of the entry.</param>
/// <param name="Action">What happened (<see cref="ScheduleAction.Removed"/>, unchanged or failed).</param>
/// <param name="Error">Failure reason; null unless <see cref="Action"/> is failed.</param>
public sealed record ScheduleRemoveOutcome(
    string PipelineFileName,
    string Cron,
    string EntryName,
    ScheduleAction Action,
    string? Error = null);

/// <summary>Status of one declared (pipeline, cron) schedule.</summary>
/// <param name="PipelineFileName">Pipeline file name.</param>
/// <param name="Cron">The cron expression.</param>
/// <param name="State">Installed, missing, drifted or unsupported.</param>
/// <param name="EntryName">Location/identity of the entry when one exists.</param>
/// <param name="Error">Validation or representation error naming the pipeline and expression.</param>
public sealed record ScheduleStatusEntry(
    string PipelineFileName,
    string Cron,
    ScheduleState State,
    string? EntryName,
    string? Error = null);

/// <summary>Aggregate result of a reconciliation pass.</summary>
/// <param name="Entries">One outcome per created/updated/unchanged/removed/failed entry.</param>
public sealed record ScheduleReconcileResult(IReadOnlyList<ScheduleInstallOutcome> Entries)
{
    /// <summary>
    /// Gets a value that indicates whether at least one entry could not be materialized.
    /// </summary>
    public bool AnyFailed => Entries.Any(entry => entry.Action == ScheduleAction.Failed);

    /// <summary>
    /// Gets a value that indicates whether at least one entry was created or refreshed.
    /// </summary>
    public bool AnyChanged => Entries.Any(entry =>
        entry.Action is ScheduleAction.Created or ScheduleAction.Updated or ScheduleAction.Removed);
}
