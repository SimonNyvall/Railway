using System.Security.Cryptography;
using System.Text;

using Railway.Engine.Domain;

namespace Railway.Engine.Helpers;

/// <summary>
/// Pure path resolution for hook artifacts: repo-local <c>.railway/hooks</c> (generated wrapper
/// scripts) and <c>.railway/logs</c> (per-run logs), plus the per-OS app-data lock root used by
/// <see cref="HookLock"/>.
/// </summary>
/// <remarks>
/// Every path is derived from the repository path passed in by the caller — nothing here ever reads
/// the current working directory. The lock root lives in the same per-OS application-data area as
/// the SQLite database (<see cref="DatabasePathHelper"/>), never inside the repository, so a checked-out
/// repo never inherits stale locks; <see cref="LockRoot"/> accepts a test override.
/// </remarks>
public sealed class HookPaths
{
    /// <summary>Name of the repo-local directory holding generated wrapper scripts.</summary>
    public const string HooksFolderName = "hooks";

    /// <summary>Name of the repo-local directory holding per-run log files.</summary>
    public const string LogsFolderName = "logs";

    /// <summary>Name of the app-data directory holding hook lock files.</summary>
    public const string LocksFolderName = "hooks-locks";

    /// <summary>
    /// Initializes a new instance of the <see cref="HookPaths"/> class.
    /// </summary>
    /// <param name="lockRootOverride">
    /// Explicit lock root (tests point this at a temp directory); when null the per-OS
    /// application-data lock root (&lt;appdata&gt;/Railway/hooks-locks) is used.
    /// </param>
    public HookPaths(string? lockRootOverride = null)
    {
        LockRoot = lockRootOverride ?? Path.Combine(DatabasePathHelper.Get(), LocksFolderName);
    }

    /// <summary>Absolute directory under which <c>&lt;repoSlug&gt;/&lt;pipelineSlug&gt;.lock</c> files live.</summary>
    public string LockRoot { get; }

    /// <summary>Resolves the repo-local wrapper-script directory (<c>&lt;repo&gt;/.railway/hooks</c>).</summary>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    public string GetHooksDirectory(string repositoryPath) =>
        Path.Combine(Normalize(repositoryPath), RepositoryHelper.RailwayFolderName, HooksFolderName);

    /// <summary>Resolves the repo-local run-log directory (<c>&lt;repo&gt;/.railway/logs</c>).</summary>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    public string GetLogsDirectory(string repositoryPath) =>
        Path.Combine(Normalize(repositoryPath), RepositoryHelper.RailwayFolderName, LogsFolderName);

    /// <summary>Resolves the generated wrapper script for a trigger event (<c>.railway/hooks/post-commit.sh</c>).</summary>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    /// <param name="triggerEvent">The event the wrapper invokes.</param>
    public string GetWrapperScriptPath(string repositoryPath, TriggerEvent triggerEvent) =>
        Path.Combine(GetHooksDirectory(repositoryPath), HookScriptHelper.GetEventKeyword(triggerEvent) + ".sh");

    /// <summary>
    /// Slug uniquely identifying this checkout in lock paths, crontab markers, launchd labels and
    /// schtasks names: the directory name plus a short deterministic hash of the normalized absolute
    /// path (<c>&lt;dirslug&gt;-&lt;8 hex&gt;</c>). The readable prefix keeps entries recognizable while
    /// the hash keeps two same-named checkouts (e.g. two clones of <c>api</c>) from colliding.
    /// Stable for the same repository path across calls, which sync/uninstall idempotency requires.
    /// </summary>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    public string GetRepositorySlug(string repositoryPath)
    {
        var normalized = Normalize(repositoryPath);
        var directorySlug = HookLock.Slugify(new DirectoryInfo(normalized).Name);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));

        return $"{directorySlug}-{Convert.ToHexString(digest)[..8].ToLowerInvariant()}";
    }

    /// <summary>Slug identifying a pipeline (derived from its file name without extension).</summary>
    /// <param name="pipelineFileName">Pipeline file name, e.g. <c>build.yaml</c>.</param>
    public string GetPipelineSlug(string pipelineFileName) =>
        HookLock.Slugify(Path.GetFileNameWithoutExtension(pipelineFileName));

    /// <summary>Full path of the lock file guarding a (repo, pipeline) pair.</summary>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    /// <param name="pipelineFileName">Pipeline file name, e.g. <c>build.yaml</c>.</param>
    public string GetLockFilePath(string repositoryPath, string pipelineFileName) =>
        Path.Combine(LockRoot, GetRepositorySlug(repositoryPath), GetPipelineSlug(pipelineFileName) + ".lock");

    /// <summary>
    /// Builds a deterministic per-run log file name:
    /// <c>run-&lt;timestamp&gt;-&lt;pipelineSlug&gt;-&lt;event&gt;.log</c>.
    /// </summary>
    /// <param name="logsDirectory">The logs directory (see <see cref="GetLogsDirectory"/>).</param>
    /// <param name="timestamp">Run start time (UTC) encoded into the name.</param>
    /// <param name="pipelineFileName">Pipeline file name, e.g. <c>build.yaml</c>.</param>
    /// <param name="triggerEvent">The event that triggered the run.</param>
    public string GetRunLogFilePath(
        string logsDirectory,
        DateTimeOffset timestamp,
        string pipelineFileName,
        TriggerEvent triggerEvent) =>
        Path.Combine(
            logsDirectory,
            $"run-{timestamp.UtcDateTime:yyyyMMddTHHmmssfff}-{GetPipelineSlug(pipelineFileName)}-{HookScriptHelper.GetEventKeyword(triggerEvent)}.log");

    private static string Normalize(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
    }
}
