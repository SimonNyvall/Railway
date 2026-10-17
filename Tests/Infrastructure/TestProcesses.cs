using System.Diagnostics;

namespace Tests.Infrastructure;

/// <summary>
/// Helpers for tests that spawn real processes: a portable long-running leaf command, the platform
/// shell invocation around it, and polling helpers for a pid file / process disappearance.
/// </summary>
internal static class TestProcesses
{
    /// <summary>Executable of the platform shell (<c>sh</c> / <c>cmd.exe</c>).</summary>
    public static string ShellFileName => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    /// <summary>The shell's "run this script" flag (<c>-c</c> / <c>/c</c>) plus the script.</summary>
    /// <param name="script">Shell script to run.</param>
    public static IReadOnlyList<string> ShellArguments(string script) =>
        [OperatingSystem.IsWindows() ? "/c" : "-c", script];

    /// <summary>A <see cref="ProcessStartInfo"/> running <paramref name="script"/> through the platform shell.</summary>
    /// <param name="script">Shell script to run.</param>
    public static ProcessStartInfo ShellScript(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ShellFileName,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in ShellArguments(script))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    /// <summary>
    /// A long-running leaf command usable inside a shell script (for <c>sh "-c"</c> /
    /// <c>cmd "/c"</c>), e.g. as a step command or a detached child's workload.
    /// </summary>
    public static string LongRunningLeaf => OperatingSystem.IsWindows()
        ? "ping -n 300 127.0.0.1 > nul"
        : "sleep 300";

    /// <summary>
    /// Starts the long-running leaf in the background, writes its pid to <paramref name="pidFilePath"/>
    /// and waits for it. POSIX only: <c>$$</c>/<c>$!</c> are not available to cmd.exe.
    /// </summary>
    /// <param name="pidFilePath">File the leaf's process id is written to.</param>
    public static Process StartBackgroundLeaf(string pidFilePath)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Background-leaf pid capture is POSIX-only.");
        }

        return Process.Start(ShellScript($"{LongRunningLeaf} & echo $! > \"{pidFilePath}\"; wait"))!;
    }

    /// <summary>
    /// Waits until the process with <paramref name="processId"/> is gone (or can no longer be
    /// observed), polling every 50 ms.
    /// </summary>
    /// <param name="processId">Process id to watch.</param>
    /// <param name="timeout">How long to wait before giving up.</param>
    public static async Task<bool> WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            try
            {
                using var process = Process.GetProcessById(processId);

                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true; // No such process: gone and reaped.
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(50);
        }
    }

    /// <summary>True while the process with <paramref name="processId"/> is still running.</summary>
    /// <param name="processId">Process id to check.</param>
    public static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Polls <paramref name="pidFilePath"/> until a pid appears in it, then returns it.
    /// </summary>
    /// <param name="pidFilePath">File the pid is written to.</param>
    /// <param name="timeout">How long to wait before giving up.</param>
    /// <exception cref="TimeoutException">When no pid was written in time.</exception>
    public static async Task<int> ReadProcessIdAsync(string pidFilePath, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(pidFilePath))
            {
                var text = (await File.ReadAllTextAsync(pidFilePath)).Trim();

                if (int.TryParse(text, out var processId))
                {
                    return processId;
                }
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"No process id was written to '{pidFilePath}' within {timeout}.");
    }
}
