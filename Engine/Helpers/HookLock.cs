using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Railway.Engine.Helpers;

/// <summary>
/// A per (repo, pipeline) mutual-exclusion lock used to prevent concurrent hook-triggered runs
/// of the same pipeline. The lock is backed by a file created atomically with
/// <see cref="FileMode.CreateNew"/>; a held lock whose owning process is dead or whose age
/// exceeds a threshold is considered stale and may be broken, so a crash never leaves a
/// pipeline blocked forever.
/// </summary>
public sealed class HookLock : IDisposable
{
    /// <summary>
    /// Default age after which a held lock is considered stale regardless of liveness of its pid.
    /// </summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromHours(6);

    private const int MaxAttempts = 3;

    /// <summary>
    /// In-process serialization for <see cref="TryAcquire"/>. The cross-process primitive is the
    /// atomic <see cref="FileMode.CreateNew"/> plus the atomic rename in <see cref="TryBreakStale"/>;
    /// this striped monitor additionally keeps same-process contenders from interleaving the
    /// create/break/create sequence (which would otherwise open several acquisition windows).
    /// </summary>
    private static readonly object[] AcquireLocks = CreateAcquireLocks();

    private bool _released;

    private HookLock(string lockFilePath)
    {
        LockFilePath = lockFilePath;
    }

    /// <summary>
    /// Full path of the backing lock file (&lt;lockRoot&gt;/&lt;repoSlug&gt;/&lt;pipelineSlug&gt;.lock).
    /// </summary>
    public string LockFilePath { get; }

    /// <summary>
    /// Attempts to acquire the (repo, pipeline) lock.
    /// </summary>
    /// <param name="lockRoot">Directory under which lock files live (resolved by the caller).</param>
    /// <param name="repoSlug">Safe file-name slug identifying the repository.</param>
    /// <param name="pipelineSlug">Safe file-name slug identifying the pipeline.</param>
    /// <param name="staleAfter">Age after which a held lock is treated as stale; <see cref="DefaultStaleAfter"/> when null.</param>
    /// <returns>A held lock that must be <see cref="Release"/>d / disposed, or null when the lock is held by a live process.</returns>
    public static HookLock? TryAcquire(string lockRoot, string repoSlug, string pipelineSlug, TimeSpan? staleAfter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineSlug);

        var threshold = staleAfter ?? DefaultStaleAfter;
        var directory = Path.Combine(lockRoot, repoSlug);
        var lockFilePath = Path.Combine(directory, pipelineSlug + ".lock");

        lock (AcquireLockFor(lockFilePath))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (IOException)
            {
                // Cannot even create the lock directory: treat as "cannot acquire", never throw.
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                try
                {
                    var stream = new FileStream(lockFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    try
                    {
                        WriteLockInfo(stream);
                    }
                    finally
                    {
                        stream.Dispose();
                    }

                    return new HookLock(lockFilePath);
                }
                catch (IOException) when (File.Exists(lockFilePath))
                {
                    // Someone holds the lock. Break it only if it is provably stale.
                    if (!IsStale(lockFilePath, threshold))
                    {
                        return null;
                    }

                    // Claim the stale lock with an atomic rename so exactly one racing process can break
                    // a given stale lock. A loser gives up rather than deleting another process's lock.
                    if (!TryBreakStale(lockFilePath, threshold))
                    {
                        return null;
                    }

                    // The stale lock is gone; retry CreateNew to take ownership.
                }
                catch (IOException)
                {
                    // Any other IO failure (e.g. a path that is not a file) means we cannot acquire.
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
            }

            return null;
        }
    }

    private static object[] CreateAcquireLocks()
    {
        var locks = new object[Math.Max(8, Environment.ProcessorCount * 4)];

        for (var i = 0; i < locks.Length; i++)
        {
            locks[i] = new object();
        }

        return locks;
    }

    private static object AcquireLockFor(string lockFilePath)
    {
        var hash = StringComparer.Ordinal.GetHashCode(lockFilePath);
        return AcquireLocks[(hash & 0x7FFFFFFF) % AcquireLocks.Length];
    }

    /// <summary>
    /// Atomically claims and removes a stale lock file. The rename is the claim: exactly one racing
    /// process can move a given file, so two processes can never both "break" the same stale lock.
    /// If the moved file turns out to be a fresh lock that raced in between the staleness check and
    /// the move, it is put back untouched, so a live holder's lock is never destroyed.
    /// </summary>
    /// <param name="lockFilePath">Path of the stale lock file.</param>
    /// <param name="staleAfter">Age after which a held lock is treated as stale.</param>
    /// <returns><see langword="true" /> when this caller won the claim; otherwise, <see langword="false" />.</returns>
    private static bool TryBreakStale(string lockFilePath, TimeSpan staleAfter)
    {
        var claimedPath = lockFilePath + ".stale-" + Guid.NewGuid().ToString("N");

        try
        {
            File.Move(lockFilePath, claimedPath);
        }
        catch (IOException)
        {
            // Another process broke or re-acquired it first.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (!IsStale(claimedPath, staleAfter))
        {
            RestoreClaim(claimedPath, lockFilePath);
            return false;
        }

        TryDelete(claimedPath);
        return true;
    }

    /// <summary>Puts a wrongly claimed fresh lock back, or drops the copy when the path was re-filled.</summary>
    private static void RestoreClaim(string claimedPath, string lockFilePath)
    {
        try
        {
            File.Move(claimedPath, lockFilePath);
        }
        catch (IOException)
        {
            // The lock path was re-created while we held the claim: leave the new lock in place.
            TryDelete(claimedPath);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(claimedPath);
        }
    }

    /// <summary>Deletes a path, swallowing the expected filesystem failures (best effort).</summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Already removed.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Turns an arbitrary path or file name into a safe file-name slug:
    /// lowercase, runs of non-alphanumeric characters collapse into a single '-',
    /// leading/trailing '-' trimmed. Returns "unnamed" when nothing survives.
    /// </summary>
    public static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unnamed";
        }

        var builder = new StringBuilder(value.Length);
        var pendingDash = false;

        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingDash = false;
                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                pendingDash = true;
            }
        }

        return builder.Length == 0 ? "unnamed" : builder.ToString();
    }

    /// <summary>
    /// Releases the lock so the pipeline becomes acquirable again. Idempotent.
    /// </summary>
    public void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;

        try
        {
            File.Delete(LockFilePath);
        }
        catch (IOException)
        {
            // Already removed (e.g. broken as stale by another process); nothing to do.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort; the stale rules guarantee the lock cannot block forever.
        }
    }

    /// <inheritdoc />
    public void Dispose() => Release();

    private static void WriteLockInfo(FileStream stream)
    {
        var content = string.Create(
            CultureInfo.InvariantCulture,
            $"{Environment.ProcessId}\n{DateTimeOffset.UtcNow:O}\n");

        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
    }

    private static bool IsStale(string lockFilePath, TimeSpan staleAfter)
    {
        int? pid = null;
        DateTimeOffset? acquiredAt = null;

        try
        {
            var lines = File.ReadAllLines(lockFilePath);

            if (lines.Length > 0 && int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPid))
            {
                pid = parsedPid;
            }

            if (lines.Length > 1 && DateTimeOffset.TryParse(lines[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedAt))
            {
                acquiredAt = parsedAt;
            }
        }
        catch (IOException)
        {
            // Concurrently deleted/replaced; treat as stale so the caller retries CreateNew.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Unreadable lock: treat as stale so the caller attempts the atomic break (which will
            // itself fail safely if the lock cannot be moved).
            return true;
        }

        if (acquiredAt is null)
        {
            // Fall back to the file's age when the content is unparseable.
            acquiredAt = File.GetLastWriteTimeUtc(lockFilePath);
        }

        if (DateTimeOffset.UtcNow - acquiredAt.Value > staleAfter)
        {
            return true;
        }

        // A live pid plus a fresh timestamp means the lock is genuinely held.
        return pid is int holderPid && !IsProcessAlive(holderPid);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with this id → dead.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
