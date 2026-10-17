using System.ComponentModel;
using System.Diagnostics;

namespace Railway.Engine.Infrastructure;

/// <summary>
/// Default <see cref="ISpawnedProcessTracker"/>: keeps the <see cref="Process"/> handles of detached
/// children in a thread-safe list, drops them as they exit, and kills their trees on demand.
/// </summary>
public sealed class SpawnedProcessTracker : ISpawnedProcessTracker
{
    private readonly object _gate = new();
    private readonly List<Process> _tracked = [];

    /// <inheritdoc />
    public int ProcessCount
    {
        get
        {
            lock (_gate)
            {
                return _tracked.Count;
            }
        }
    }

    /// <inheritdoc />
    public void Track(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (_gate)
        {
            _tracked.Add(process);
        }

        // Untrack (and release the handle) the moment the child exits on its own, so a long-running
        // host neither accumulates dead Process objects nor can ever kill a recycled process id.
        process.Exited += OnExited;

        try
        {
            process.EnableRaisingEvents = true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Already reaped/gone: there is nothing left to track.
            Untrack(process);
        }
    }

    /// <inheritdoc />
    public void Untrack(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (_gate)
        {
            _tracked.Remove(process);
        }

        process.Dispose();
    }

    /// <inheritdoc />
    public int KillTracked()
    {
        Process[] snapshot;

        lock (_gate)
        {
            snapshot = [.. _tracked];
            _tracked.Clear();
        }

        var killed = 0;

        foreach (var process in snapshot)
        {
            try
            {
                if (ProcessTreeKiller.KillQuietly(process))
                {
                    killed++;
                }
            }
            finally
            {
                // Dispose is idempotent, so a concurrent Exited handler (which also disposes) is fine.
                process.Dispose();
            }
        }

        return killed;
    }

    private void OnExited(object? sender, EventArgs eventArgs)
    {
        if (sender is Process process)
        {
            Untrack(process);
        }
    }
}
