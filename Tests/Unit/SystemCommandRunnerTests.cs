using System.Diagnostics;

using Railway.Engine.Infrastructure;

using Tests.Infrastructure;

using Xunit;

namespace Tests.Unit;

/// <summary>
/// Tests for the production <see cref="SystemCommandRunner"/> against real processes: blocking runs
/// and the detached path the CLI uses for <c>hooks run --detach</c> (including the tracker handoff
/// that makes a cancelled detached child killable).
/// </summary>
public class SystemCommandRunnerTests
{
    [Fact]
    public void Run_CapturesStdoutAndExitCode()
    {
        var runner = new SystemCommandRunner();

        var result = runner.Run(
            TestProcesses.ShellFileName,
            TestProcesses.ShellArguments("echo hello-railway"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello-railway", result.StandardOutput);
        Assert.Null(result.ProcessId);
    }

    [Fact]
    public void Run_MissingExecutable_ReportsNotFoundWithoutThrowing()
    {
        var runner = new SystemCommandRunner();

        var result = runner.Run("railway-test-definitely-missing-executable", []);

        Assert.Equal(SystemCommandResult.ExecutableNotFoundExitCode, result.ExitCode);
        Assert.False(result.Executed);
    }

    [Fact]
    public async Task RunDetached_TracksTheChildSoItCanBeKilledOnCancellation()
    {
        var tracker = new SpawnedProcessTracker();
        var runner = new SystemCommandRunner(tracker);

        var result = runner.RunDetached(
            TestProcesses.ShellFileName,
            TestProcesses.ShellArguments(TestProcesses.LongRunningLeaf));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.ProcessId);
        Assert.Equal(1, tracker.ProcessCount);

        // The CLI's Ctrl+C path is CancellationToken.Register(KillTracked): the detached child is
        // gone once it has run.
        Assert.Equal(1, tracker.KillTracked());

        Assert.True(
            await TestProcesses.WaitForProcessExitAsync(result.ProcessId!.Value, TimeSpan.FromSeconds(10)),
            "the tracked detached child should be terminated by KillTracked");
        Assert.Equal(0, tracker.ProcessCount);
    }

    [Fact]
    public void RunDetached_WithoutTracker_StillStartsTheChildAndReportsItsId()
    {
        var runner = new SystemCommandRunner();

        var result = runner.RunDetached(
            TestProcesses.ShellFileName,
            TestProcesses.ShellArguments("exit 0"));

        Assert.Equal(0, result.ExitCode);
        Assert.NotEqual(0, result.ProcessId);
    }

    [Fact]
    public void RunDetached_MissingExecutable_ReportsNotFoundWithoutThrowing()
    {
        var tracker = new SpawnedProcessTracker();
        var runner = new SystemCommandRunner(tracker);

        var result = runner.RunDetached("railway-test-definitely-missing-executable", []);

        Assert.Equal(SystemCommandResult.ExecutableNotFoundExitCode, result.ExitCode);
        Assert.Null(result.ProcessId);
        Assert.Equal(0, tracker.ProcessCount);
    }

    [Fact]
    public async Task RunDetached_ChildThatExits_FreesItsTrackingSlot()
    {
        var tracker = new SpawnedProcessTracker();
        var runner = new SystemCommandRunner(tracker);

        var result = runner.RunDetached(
            TestProcesses.ShellFileName,
            TestProcesses.ShellArguments("exit 0"));

        Assert.Equal(0, result.ExitCode);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (tracker.ProcessCount != 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, tracker.ProcessCount);
    }

    [Fact]
    public void Run_Timeout_KillsTheProcessTree()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses POSIX sh background pid capture");

        using var directory = new TempDirectory();
        var runner = new SystemCommandRunner();

        var result = runner.Run(
            TestProcesses.ShellFileName,
            TestProcesses.ShellArguments($"{TestProcesses.LongRunningLeaf} & echo $! > \"{directory.File("pid")}\"; wait"),
            timeoutMs: 3_000);

        Assert.Equal(SystemCommandResult.TimedOutExitCode, result.ExitCode);

        var leafId = ReadPidEventually(directory.File("pid"));
        Assert.NotEqual(-1, leafId);
        Assert.False(TestProcesses.IsRunning(leafId), "the timeout kill should take the whole tree down");
    }

    private static int ReadPidEventually(string pidFilePath)
    {
        try
        {
            return TestProcesses.ReadProcessIdAsync(pidFilePath, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            return -1;
        }
    }
}
