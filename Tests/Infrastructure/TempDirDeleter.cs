using Microsoft.Data.Sqlite;

namespace Tests.Infrastructure;

/// <summary>
/// Deletes temp directories that may contain SQLite databases. Microsoft.Data.Sqlite pools
/// connections by default (since v9), keeping the physical connection (and the db file handle)
/// open after DbContext/ServiceProvider disposal — so a plain Directory.Delete throws
/// IOException on Windows. Clearing the pools first, plus a short retry, fixes that.
/// </summary>
internal static class TempDirDeleter
{
    private const int MaxAttempts = 3;
    private const int RetryDelayMs = 200;

    /// <summary>
    /// Clears all pooled SQLite connections, then deletes the directory (recursive).
    /// No-op if the directory does not exist. Retries transient IOExceptions
    /// (e.g. file handle still releasing); rethrows after the last attempt.
    /// </summary>
    public static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();

        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                Thread.Sleep(RetryDelayMs);
                SqliteConnection.ClearAllPools();
            }
        }
    }
}
