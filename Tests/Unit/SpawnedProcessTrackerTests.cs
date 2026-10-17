using System.Diagnostics;

using Railway.Engine.Infrastructure;

using Tests.Infrastructure;

using Xunit;

namespace Tests.Unit;

/// <summary>
/// Tests for <see cref="SpawnedProcessTracker"/> against real processes: tracking, automatic
/// untracking when a child exits, and tree-killing (the cancellation path the CLI uses on Ctrl+C).
/// </summary>
public class SpawnedProcessTrackerTests
{
    [Fact]
    public void KillTracked_EmptyTracker_ReturnsZeroAndDoesNotThrow()
    {
        var tracker = new SpawnedProcessTracker();

        Assert.Equal(0, tracker.ProcessCount);
        Assert.Equal(0, tracker.KillTracked());
    }

    [Fact]
    public async Task KillTracked_KillsTheWholeTrackedProcessTree()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses POSIX sh background pid capture");

        using var directory = new TempDirectory();
        var tracker = new SpawnedProcessTracker();

        // The tracked child is a shell whose leaf is a grandchild: killing only the direct child
        // would leave the leaf (the actual work) running.
        using var child = TestProcesses.StartBackgroundLeaf(directory.File("pid"));

        tracker.Track(child);
        Assert.Equal(1, tracker.ProcessCount);

        var leafId = await TestProcesses.ReadProcessIdAsync(directory.File("pid"), TimeSpan.FromSeconds(10));
        Assert.True(TestProcesses.IsRunning(leafId), "the background leaf should be running before the kill");

        Assert.Equal(1, tracker.KillTracked());

        Assert.True(
            await TestProcesses.WaitForProcessExitAsync(leafId, TimeSpan.FromSeconds(10)),
            "KillTracked should terminate the tracked child's descendants too");
        Assert.Equal(0, tracker.ProcessCount);
    }

    [Fact]
    public void KillTracked_Twice_IsIdempotent()
    {
        using var child = Process.Start(TestProcesses.ShellScript(TestProcesses.LongRunningLeaf))!;

        var tracker = new SpawnedProcessTracker();
        tracker.Track(child);

        Assert.Equal(1, tracker.KillTracked());
        Assert.Equal(0, tracker.KillTracked());
        Assert.Equal(0, tracker.ProcessCount);
    }

    [Fact]
    public async Task Track_ChildThatExitsOnItsOwn_IsUntrackedAutomatically()
    {
        using var child = Process.Start(TestProcesses.ShellScript("exit 0"))!;

        var tracker = new SpawnedProcessTracker();
        tracker.Track(child);

        Assert.True(
            await WaitForAsync(() => tracker.ProcessCount == 0, TimeSpan.FromSeconds(10)),
            "an exited child must stop being tracked (the handle is released)");

        Assert.Equal(0, tracker.KillTracked());
    }

    [Fact]
    public void Track_AlreadyExitedChild_EndsUpUntracked()
    {
        using var child = Process.Start(TestProcesses.ShellScript("exit 0"))!;
        child.WaitForExit();

        var tracker = new SpawnedProcessTracker();
        tracker.Track(child);

        Assert.True(
            WaitFor(() => tracker.ProcessCount == 0, TimeSpan.FromSeconds(10)),
            "tracking an already exited child must not leave a stale entry");
        Assert.Equal(0, tracker.KillTracked());
    }

    [Fact]
    public async Task Untrack_StopsTrackingWithoutKilling()
    {
        using var child = Process.Start(TestProcesses.ShellScript(TestProcesses.LongRunningLeaf))!;
        var childId = child.Id;

        var tracker = new SpawnedProcessTracker();
        tracker.Track(child);
        tracker.Untrack(child);

        Assert.Equal(0, tracker.ProcessCount);
        Assert.Equal(0, tracker.KillTracked());

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.True(TestProcesses.IsRunning(childId), "an untracked child must not be killed by KillTracked");

        ProcessTreeKiller.KillQuietly(child);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        return condition();
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout) =>
        WaitForAsync(condition, timeout).GetAwaiter().GetResult();
}
