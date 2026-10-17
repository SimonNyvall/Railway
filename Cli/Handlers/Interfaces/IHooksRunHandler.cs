namespace Cli.Handlers.Interfaces;

/// <summary>
/// Raw hook-context option values collected by <c>hooks run</c>. The handler turns them into a
/// <see cref="Railway.Engine.Orchestration.Interfaces.HookEventContext"/> (validating <see cref="IsSquash"/>).
/// </summary>
/// <param name="Branch">Branch name for a push/checkout event.</param>
/// <param name="Sha">Commit SHA involved in the event.</param>
/// <param name="Tag">Tag name for a tag push.</param>
/// <param name="Remote">Remote name for a push.</param>
/// <param name="Previous">Previous HEAD for a checkout/merge event (all zeros for a clone).</param>
/// <param name="New">New HEAD for a checkout/merge event.</param>
/// <param name="Flag">Raw git flag argument (e.g. <c>1</c> for a branch checkout).</param>
/// <param name="IsSquash">Raw <c>post-merge</c> squash flag (<c>true</c>/<c>false</c> or <c>1</c>/<c>0</c>).</param>
/// <param name="Cron">Cron expression of the schedule entry that fired (recorded as provenance).</param>
public sealed record HooksRunContextOptions(
    string? Branch = null,
    string? Sha = null,
    string? Tag = null,
    string? Remote = null,
    string? Previous = null,
    string? New = null,
    string? Flag = null,
    string? IsSquash = null,
    string? Cron = null);

/// <summary>
/// Renders the result of <c>railway hooks run &lt;event&gt;</c>.
/// </summary>
public interface IHooksRunHandler
{
    /// <summary>
    /// Fires <paramref name="eventKeyword"/> through the Engine hook runner and renders each
    /// pipeline's result.
    /// </summary>
    /// <param name="eventKeyword">Hook event keyword (e.g. <c>post-commit</c>).</param>
    /// <param name="pipeline">Optional single pipeline file name filter.</param>
    /// <param name="detach">When true, spawns detached children and returns immediately.</param>
    /// <param name="notify">When false, no notifications are sent.</param>
    /// <param name="path">Repository root (or any path inside it); null uses the current directory.</param>
    /// <param name="verbose">When true, renders extra detail.</param>
    /// <param name="context">Raw event-context flags used for branch/tag filtering and provenance.</param>
    /// <param name="ct">Cancellation token observed while running pipelines.</param>
    /// <returns>
    /// <see langword="true"/> when every run succeeded or was skipped (exit 0); <see langword="false"/>
    /// when any run failed, the event keyword is invalid, or the context flags are malformed (exit 1).
    /// </returns>
    Task<bool> Handle(
        string eventKeyword,
        string? pipeline,
        bool detach,
        bool notify,
        string? path,
        bool verbose,
        HooksRunContextOptions context,
        CancellationToken ct = default);
}
