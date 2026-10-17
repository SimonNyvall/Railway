using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Railway.Engine.Infrastructure;

/// <summary>
/// SQLite-specific resilience helpers. EF Core exposes no retrying execution strategy for SQLite
/// (<c>EnableRetryOnFailure</c> is gated off for the provider), so transient lock failures are retried
/// here with a bounded backoff. The <c>Default Timeout</c> connection-string keyword maps to
/// <c>sqlite3_busy_timeout</c> and absorbs most contention; this is the belt-and-braces layer for the
/// write/write races EF surfaces directly.
/// </summary>
public static class SqliteRetryHelper
{
    private const int SqliteBusy = 5; // SQLITE_BUSY
    private const int SqliteLocked = 6; // SQLITE_LOCKED
    private const int SqliteConstraint = 19; // SQLITE_CONSTRAINT
    private const int SqliteConstraintUnique = 2067; // SQLITE_CONSTRAINT_UNIQUE
    private const int SqliteConstraintPrimaryKey = 1555; // SQLITE_CONSTRAINT_PRIMARYKEY

    private const int MaxAttempts = 5;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Saves the context's tracked changes, retrying a bounded number of times when SQLite reports the
    /// database is busy or locked because a concurrent writer holds the lock.
    /// </summary>
    /// <param name="context">The context whose changes are saved.</param>
    /// <param name="cancellationToken">A token to observe while waiting.</param>
    /// <returns>The number of state entries written to the database.</returns>
    public static Task<int> SaveChangesWithRetryAsync(DbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RetryAsync(() => context.SaveChangesAsync(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="operation"/>, retrying transient SQLite busy/locked failures with a bounded
    /// linear-then-exponential backoff.
    /// </summary>
    /// <param name="operation">The operation to run.</param>
    /// <param name="cancellationToken">A token to observe while waiting.</param>
    public static async Task<T> RetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxAttempts - 1 && IsTransientSqliteError(ex))
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(MaxBackoff.TotalMilliseconds, 50 * Math.Pow(2, attempt)));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>True when the exception (or an inner one) is a transient SQLite busy/locked error.</summary>
    /// <param name="exception">The exception to inspect.</param>
    public static bool IsTransientSqliteError(Exception exception)
    {
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqliteException { SqliteErrorCode: SqliteBusy or SqliteLocked })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the update failed because of a UNIQUE or PRIMARY KEY constraint, i.e. a concurrent
    /// writer inserted the same row first. Detected through
    /// <see cref="SqliteException.SqliteExtendedErrorCode"/> rather than by matching the message.
    /// </summary>
    /// <param name="exception">The update exception to inspect.</param>
    public static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqliteException { SqliteErrorCode: SqliteConstraint } sqlite
                && sqlite.SqliteExtendedErrorCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey)
            {
                return true;
            }
        }

        return false;
    }
}
