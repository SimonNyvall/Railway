using System.ComponentModel;
using System.Diagnostics;

namespace Railway.Engine.Infrastructure;

/// <summary>
/// Best-effort termination of a process tree rooted at a child process. Shared by
/// <see cref="SystemCommandRunner"/> (timeout), <see cref="SpawnedProcessTracker"/> (cancellation of
/// detached children) and the step executor in <c>RunnerService</c> (cancellation of a running step).
/// </summary>
internal static class ProcessTreeKiller
{
    /// <summary>
    /// Kills the tree rooted at <paramref name="process"/>. Never throws: a process that already
    /// exited or cannot be accessed is simply reported as "nothing to kill".
    /// </summary>
    /// <param name="process">The child process whose tree should be terminated.</param>
    /// <returns>True when a kill was issued (the process was still alive when checked).</returns>
    public static bool KillQuietly(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            if (process.HasExited)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or Win32Exception or NotSupportedException
                or ObjectDisposedException or AggregateException)
        {
            // Not started / already exited / access denied / a descendant resisted the kill:
            // the caller is on a cancellation path, so this is not actionable.
            return false;
        }
    }
}
