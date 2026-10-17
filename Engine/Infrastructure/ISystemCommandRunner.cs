namespace Railway.Engine.Infrastructure;

/// <summary>
/// Injectable seam for running external commands (git, launchctl, crontab, schtasks,
/// osascript, notify-send, PowerShell). Hosts fake this in tests; production code uses
/// <see cref="SystemCommandRunner"/>.
/// </summary>
/// <remarks>
/// No shell is involved: the executable is invoked directly with an argument vector, so
/// callers never have to quote for a shell. Executables that cannot be started are reported
/// through <see cref="SystemCommandResult.ExecutableNotFoundExitCode"/> rather than by
/// throwing, so schedulers and notifiers can degrade gracefully.
/// </remarks>
public interface ISystemCommandRunner
{
    /// <summary>
    /// Runs an executable to completion, capturing its standard output and error.
    /// </summary>
    /// <param name="fileName">Executable to start (resolved through <c>PATH</c> when not absolute).</param>
    /// <param name="arguments">Argument vector passed verbatim to the process.</param>
    /// <param name="workingDirectory">Working directory for the child process, or null to inherit.</param>
    /// <param name="stdin">Text written to the child's standard input before it is closed, or null.</param>
    /// <param name="timeoutMs">Milliseconds to wait before the process tree is killed.</param>
    /// <returns>The process outcome; see <see cref="SystemCommandResult"/> for the special exit codes.</returns>
    SystemCommandResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        string? stdin = null,
        int timeoutMs = 30_000);

    /// <summary>
    /// Starts an executable and returns immediately without waiting for it (used by
    /// <c>hooks run --detach</c>). Output is not captured; the child is responsible for its
    /// own logging and must survive the parent exiting.
    /// </summary>
    /// <remarks>
    /// Implementations that own a process handle hand it to an <see cref="ISpawnedProcessTracker"/>
    /// when one is available, so a host that is cancelled (CLI Ctrl+C) can kill the detached tree
    /// instead of leaving an orphan behind. The handle is released as soon as the child exits.
    /// </remarks>
    /// <param name="fileName">Executable to start (resolved through <c>PATH</c> when not absolute).</param>
    /// <param name="arguments">Argument vector passed verbatim to the process.</param>
    /// <param name="workingDirectory">Working directory for the child process, or null to inherit.</param>
    /// <returns>
    /// A result whose <see cref="SystemCommandResult.ExitCode"/> is 0 when the process was
    /// started, or <see cref="SystemCommandResult.ExecutableNotFoundExitCode"/> when it could not be.
    /// </returns>
    SystemCommandResult RunDetached(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null);
}

/// <summary>
/// The outcome of an <see cref="ISystemCommandRunner"/> invocation.
/// </summary>
/// <param name="ExitCode">
/// The process exit code, or one of the sentinels <see cref="ExecutableNotFoundExitCode"/> /
/// <see cref="TimedOutExitCode"/> when the process never produced an exit code of its own.
/// </param>
/// <param name="StandardOutput">Captured standard output (empty when not captured).</param>
/// <param name="StandardError">Captured standard error; carries the reason on sentinel outcomes.</param>
/// <param name="ProcessId">
/// The child's process id when it was started (detached children only), otherwise null. Lets hosts
/// and tests observe the detached child; the handle itself lives in the <see cref="ISpawnedProcessTracker"/>.
/// </param>
public sealed record SystemCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    int? ProcessId = null)
{
    /// <summary>
    /// Exit code used when the executable could not be started (missing or not on <c>PATH</c>).
    /// Mirrors the POSIX shell "command not found" code; callers treat it as graceful degradation.
    /// </summary>
    public const int ExecutableNotFoundExitCode = 127;

    /// <summary>Exit code used when the process exceeded its timeout and was killed (GNU <c>timeout</c> convention).</summary>
    public const int TimedOutExitCode = 124;

    /// <summary>True when the process actually ran and returned its own exit code.</summary>
    public bool Executed => ExitCode is not ExecutableNotFoundExitCode and not TimedOutExitCode;

    /// <summary>Creates the not-found result for <paramref name="fileName"/>.</summary>
    /// <param name="fileName">The executable that could not be started.</param>
    public static SystemCommandResult NotFound(string fileName) =>
        new(ExecutableNotFoundExitCode, string.Empty, $"Executable not found: {fileName}");
}
