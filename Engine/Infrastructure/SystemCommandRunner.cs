using System.ComponentModel;
using System.Diagnostics;

namespace Railway.Engine.Infrastructure;

/// <summary>
/// Production <see cref="ISystemCommandRunner"/> backed by <see cref="Process"/>. Never uses a
/// shell, never writes to this process' stdout, and reports a missing executable as
/// <see cref="SystemCommandResult.ExecutableNotFoundExitCode"/> instead of throwing.
/// </summary>
public sealed class SystemCommandRunner : ISystemCommandRunner
{
    private readonly ISpawnedProcessTracker? _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemCommandRunner"/> class.
    /// </summary>
    /// <param name="tracker">
    /// Optional tracker that receives every detached child (<see cref="RunDetached"/>) so a host can
    /// kill it when it is cancelled. When null, detached children are started untracked and simply
    /// outlive this process.
    /// </param>
    public SystemCommandRunner(ISpawnedProcessTracker? tracker = null)
    {
        _tracker = tracker;
    }

    /// <inheritdoc />
    public SystemCommandResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        string? stdin = null,
        int timeoutMs = 30_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            // ENOENT/ENOTDIR on Unix, FILE_NOT_FOUND/PATH_NOT_FOUND on Windows.
            return SystemCommandResult.NotFound(fileName);
        }
        catch (Win32Exception exception)
        {
            return new SystemCommandResult(-1, string.Empty, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return new SystemCommandResult(-1, string.Empty, exception.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (stdin is not null)
        {
            try
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Child closed stdin early; its exit code still tells the caller what happened.
            }
        }

        var timedOut = false;

        try
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            ProcessTreeKiller.KillQuietly(process);
        }

        var standardOutput = ReadSafely(stdoutTask);
        var standardError = ReadSafely(stderrTask);

        if (timedOut)
        {
            return new SystemCommandResult(
                SystemCommandResult.TimedOutExitCode,
                standardOutput,
                string.IsNullOrEmpty(standardError)
                    ? $"Command '{fileName}' timed out after {timeoutMs} ms."
                    : standardError);
        }

        return new SystemCommandResult(process.ExitCode, standardOutput, standardError);
    }

    /// <inheritdoc />
    public SystemCommandResult RunDetached(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            // Windows only: give the detached child its own process group so console Ctrl+C events
            // are not delivered to it. On Unix the property is unsupported and the child shares this
            // process' group (so a terminal SIGINT reaches it as well); the tracker covers
            // cancellation that does not come from the terminal there.
            startInfo.CreateNewProcessGroup = true;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            var process = Process.Start(startInfo);

            if (process is null)
            {
                return new SystemCommandResult(-1, string.Empty, $"Failed to start '{fileName}'.");
            }

            var processId = process.Id;

            if (_tracker is null)
            {
                process.Dispose();
            }
            else
            {
                // The tracker owns the handle from here on: it releases it once the child exits, and
                // kills the tree (with the child's descendants) when the host cancels.
                _tracker.Track(process);
            }

            // Intentionally not waited for: the child outlives this process (hooks --detach).
            return new SystemCommandResult(0, string.Empty, string.Empty, processId);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return SystemCommandResult.NotFound(fileName);
        }
        catch (Win32Exception exception)
        {
            return new SystemCommandResult(-1, string.Empty, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return new SystemCommandResult(-1, string.Empty, exception.Message);
        }
    }

    private static string ReadSafely(Task<string> read)
    {
        try
        {
            return read.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // A broken pipe while reading means there is simply no output to report.
            return string.Empty;
        }
    }
}
