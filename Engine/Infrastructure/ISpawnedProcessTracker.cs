using System.Diagnostics;

namespace Railway.Engine.Infrastructure;

/// <summary>
/// Tracks the processes started detached through <see cref="ISystemCommandRunner.RunDetached"/> so a
/// host can terminate them (whole process tree) when it is cancelled — the CLI does this from its
/// Ctrl+C handler.
/// </summary>
/// <remarks>
/// A detached child is meant to outlive the process that spawned it (a git hook wrapper exits long
/// before the run it detached finishes), so tracking never terminates anything by itself. It only
/// hands the host a handle for the case it must stop: cancellation. Without it, a cancelled
/// long-running child would keep running after the hook lock has already been released.
/// </remarks>
public interface ISpawnedProcessTracker
{
    /// <summary>Number of processes currently tracked (children that have not exited yet).</summary>
    int ProcessCount { get; }

    /// <summary>
    /// Starts tracking a freshly started process. The tracker takes ownership of
    /// <paramref name="process"/> and releases it once the child exits or is killed, so callers must
    /// not dispose it themselves afterwards.
    /// </summary>
    /// <param name="process">A process that was just started (and not yet disposed).</param>
    void Track(Process process);

    /// <summary>Stops tracking a process and releases its handle.</summary>
    /// <param name="process">The tracked process to forget.</param>
    void Untrack(Process process);

    /// <summary>
    /// Kills the process tree of every tracked process and stops tracking it. Children that already
    /// exited are forgotten silently. Never throws — this is a cancellation path.
    /// </summary>
    /// <returns>The number of process trees that were actually killed.</returns>
    int KillTracked();
}
