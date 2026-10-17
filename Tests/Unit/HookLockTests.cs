using Railway.Engine.Helpers;

using Xunit;

namespace Tests.Unit;

public sealed class HookLockTests : IDisposable
{
    private readonly string _lockRoot = Path.Combine(
        Path.GetTempPath(),
        "railway-hooklock-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_lockRoot))
            {
                Directory.Delete(_lockRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public void TryAcquire_FreeLock_AcquiresAndWritesPidAndTimestamp()
    {
        using var handle = HookLock.TryAcquire(_lockRoot, "my-repo", "build");

        Assert.NotNull(handle);
        Assert.True(File.Exists(handle.LockFilePath));
        Assert.Equal(Path.Combine(_lockRoot, "my-repo", "build.lock"), handle.LockFilePath);

        var lines = File.ReadAllLines(handle.LockFilePath);
        Assert.Equal(Environment.ProcessId.ToString(), lines[0]);
        Assert.True(DateTimeOffset.TryParse(lines[1], out var acquiredAt));
        Assert.True((DateTimeOffset.UtcNow - acquiredAt).Duration() < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void TryAcquire_WhileHeldByLiveProcess_ReturnsNull()
    {
        using var held = HookLock.TryAcquire(_lockRoot, "my-repo", "build");
        Assert.NotNull(held);

        var contender = HookLock.TryAcquire(_lockRoot, "my-repo", "build");

        Assert.Null(contender);
    }

    [Fact]
    public void TryAcquire_LockWithFreshTimestampAndDeadPid_ReturnsNull()
    {
        // A lock with a live pid (ours) and a fresh timestamp must never be broken.
        WriteLockManually("my-repo", "build", Environment.ProcessId, DateTimeOffset.UtcNow);

        var contender = HookLock.TryAcquire(_lockRoot, "my-repo", "build");

        Assert.Null(contender);
    }

    [Fact]
    public void TryAcquire_StaleByAge_ReplacesAndAcquires()
    {
        var oldTimestamp = DateTimeOffset.UtcNow - TimeSpan.FromHours(7);
        WriteLockManually("my-repo", "build", Environment.ProcessId, oldTimestamp);

        using var handle = HookLock.TryAcquire(_lockRoot, "my-repo", "build");

        Assert.NotNull(handle);

        // The stale lock must have been replaced with fresh ownership.
        var lines = File.ReadAllLines(handle.LockFilePath);
        Assert.Equal(Environment.ProcessId.ToString(), lines[0]);
        Assert.True(DateTimeOffset.TryParse(lines[1], out var acquiredAt));
        Assert.NotEqual(oldTimestamp, acquiredAt);
    }

    [Fact]
    public void TryAcquire_StaleByCustomAgeThreshold_Replaces()
    {
        WriteLockManually("my-repo", "build", Environment.ProcessId, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));

        using var handle = HookLock.TryAcquire(_lockRoot, "my-repo", "build", TimeSpan.FromMinutes(5));

        Assert.NotNull(handle);
    }

    [Fact]
    public void TryAcquire_StaleByDeadPid_ReplacesAndAcquires()
    {
        // int.MaxValue (2147483647) is far beyond any plausible pid on all supported platforms.
        const int deadPid = int.MaxValue;
        WriteLockManually("my-repo", "build", deadPid, DateTimeOffset.UtcNow);

        using var handle = HookLock.TryAcquire(_lockRoot, "my-repo", "build");

        Assert.NotNull(handle);

        var lines = File.ReadAllLines(handle.LockFilePath);
        Assert.Equal(Environment.ProcessId.ToString(), lines[0]);
    }

    [Fact]
    public void TryAcquire_AfterRelease_Succeeds()
    {
        var held = HookLock.TryAcquire(_lockRoot, "my-repo", "build");
        Assert.NotNull(held);

        held.Release();

        Assert.False(File.Exists(held.LockFilePath));

        using var reacquired = HookLock.TryAcquire(_lockRoot, "my-repo", "build");

        Assert.NotNull(reacquired);
    }

    [Fact]
    public void Dispose_ReleasesLock()
    {
        var handle = HookLock.TryAcquire(_lockRoot, "my-repo", "build");
        Assert.NotNull(handle);

        handle.Dispose();

        Assert.False(File.Exists(handle.LockFilePath));

        using var reacquired = HookLock.TryAcquire(_lockRoot, "my-repo", "build");

        Assert.NotNull(reacquired);
    }

    [Fact]
    public void Release_IsIdempotent()
    {
        var handle = HookLock.TryAcquire(_lockRoot, "my-repo", "build");
        Assert.NotNull(handle);

        handle.Release();
        handle.Release();
        handle.Dispose();

        Assert.False(File.Exists(handle.LockFilePath));
    }

    [Fact]
    public void TryAcquire_DifferentPipelines_DoNotContend()
    {
        using var first = HookLock.TryAcquire(_lockRoot, "my-repo", "build");
        using var second = HookLock.TryAcquire(_lockRoot, "my-repo", "test");
        using var third = HookLock.TryAcquire(_lockRoot, "other-repo", "build");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
    }

    // ---------------------------------------------------------------------- B3: robustness

    [Fact]
    public void TryAcquire_LockPathBlockedByDirectory_ReturnsNullWithoutThrowing()
    {
        // A directory where the lock file should be makes CreateNew fail with an IO/permission error
        // and File.Exists(lockFilePath) is false. Acquisition must degrade to "cannot acquire".
        var directory = Path.Combine(_lockRoot, "my-repo");
        Directory.CreateDirectory(Path.Combine(directory, "build.lock"));

        var handle = HookLock.TryAcquire(_lockRoot, "my-repo", "build");

        Assert.Null(handle);
    }

    [Fact]
    public async Task TryAcquire_ConcurrentStaleBreaks_ExactlyOneWins()
    {
        WriteLockManually("my-repo", "build", Environment.ProcessId, DateTimeOffset.UtcNow - TimeSpan.FromHours(7));

        const int contenders = 8;
        using var barrier = new Barrier(contenders);
        var handles = new HookLock?[contenders];
        var tasks = new Task[contenders];

        for (var i = 0; i < contenders; i++)
        {
            var index = i;
            tasks[i] = Task.Run(
                () =>
                {
                    barrier.SignalAndWait(TestContext.Current.CancellationToken);
                    handles[index] = HookLock.TryAcquire(_lockRoot, "my-repo", "build");
                },
                TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(tasks);

        var winners = handles.Where(handle => handle is not null).ToList();
        Assert.Single(winners);

        foreach (var winner in winners)
        {
            winner!.Release();
            winner.Dispose();
        }
    }

    [Theory]
    [InlineData("/Users/dev/My Repo (v2)", "users-dev-my-repo-v2")]
    [InlineData("Build.Deploy!!", "build-deploy")]
    [InlineData("---", "unnamed")]
    [InlineData("", "unnamed")]
    [InlineData("UPPER_case-123", "upper-case-123")]
    public void Slugify_ProducesSafeLowercaseSlugs(string input, string expected)
    {
        Assert.Equal(expected, HookLock.Slugify(input));
    }

    private void WriteLockManually(string repoSlug, string pipelineSlug, int pid, DateTimeOffset acquiredAt)
    {
        var directory = Path.Combine(_lockRoot, repoSlug);
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, pipelineSlug + ".lock"),
            $"{pid}\n{acquiredAt:O}\n");
    }
}
