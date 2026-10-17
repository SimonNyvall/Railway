using Microsoft.Extensions.DependencyInjection;

using Railway.Engine;
using Railway.Engine.Helpers;
using Railway.Engine.Orchestration;
using Railway.Engine.Orchestration.Interfaces;

using Tests.Infrastructure;

using Xunit;

namespace Tests.Integration;

/// <summary>
/// Phase 4 <c>hooks watch</c> integration tests: a real temp git repository + temp SQLite database, a
/// real <c>FileSystemWatcher</c> and the real Engine pipeline runner. A temp file write drives the
/// watcher; assertions poll the database with generous timeouts (never fixed-sleep as the primary
/// synchronization) and verify a debounced burst produces exactly one run, while <c>--initial</c>
/// adds a second (immediate) run.
/// </summary>
[Collection("Integration Tests")]
public sealed class HooksWatchTests : IDisposable
{
    private const int PollTimeoutMs = 30_000;

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(1_200);

    private readonly TestDatabaseFixture _database = new();
    private readonly TestRailwayRepository _repository;
    private readonly string _originalDir;
    private readonly string _lockRoot;

    public HooksWatchTests()
    {
        _originalDir = Directory.GetCurrentDirectory();
        _repository = new TestRailwayRepository();
        _lockRoot = Path.Combine(Path.GetTempPath(), "railway_watch_locks_" + Guid.NewGuid().ToString("N"));

        _repository.WritePipeline("build.yaml", WatchPipeline);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        _repository.Dispose();
        _database.Dispose();

        TryDeleteDirectory(_lockRoot);
    }

    private const string WatchPipeline = """
        name: build
        triggers:
          - watch: ["**/*"]
        jobs:
          build:
            steps:
              - name: echo
                run: echo "watch ran"
        """;

    [Fact]
    public async Task Watch_FileWrite_RunsPipelineExactlyOnce()
    {
        using var provider = CreateProvider();
        var watcher = (HookWatcher)provider.GetRequiredService<IHookWatcher>();

        using var cts = new CancellationTokenSource();
        var watchTask = watcher.WatchAsync(
            _repository.Path,
            new HookWatchOptions(Debounce: Debounce, Notify: false),
            progress: null,
            cts.Token);

        await WaitUntilWatchingAsync(watcher);

        WriteWatchedFile("a.txt");

        var count = await WaitForRunCountAsync(1);

        Assert.True(count >= 1, "the watch pipeline should have run once for the file write");

        // Wait a full quiet window and confirm the burst coalesced to exactly one run.
        await Task.Delay(QuietWindow, TestContext.Current.CancellationToken);
        Assert.Equal(1, CountRuns());

        await StopAsync(watchTask, cts);
    }

    [Fact]
    public async Task Watch_Initial_RunsImmediatelyAndAgainOnChange()
    {
        using var provider = CreateProvider();
        var watcher = (HookWatcher)provider.GetRequiredService<IHookWatcher>();

        using var cts = new CancellationTokenSource();
        var watchTask = watcher.WatchAsync(
            _repository.Path,
            new HookWatchOptions(Debounce: Debounce, Initial: true, Notify: false),
            progress: null,
            cts.Token);

        // The immediate run happens before a single file is written.
        var initialCount = await WaitForRunCountAsync(1);
        Assert.True(initialCount >= 1, "the --initial run should have executed immediately");

        await WaitUntilWatchingAsync(watcher);

        WriteWatchedFile("a.txt");
        var afterWrite = await WaitForRunCountAsync(2);

        Assert.True(afterWrite >= 2, "the file write should add a second run after --initial");

        await Task.Delay(QuietWindow, TestContext.Current.CancellationToken);
        Assert.Equal(2, CountRuns());

        await StopAsync(watchTask, cts);
    }

    [Fact]
    public async Task Watch_Initial_SecondRunForWriteInPreexistingDirectory()
    {
        // A directory that exists before Start is inside the initial OS watch, so this case does not
        // depend on the new-directory rescan: it is the deterministic companion to the test above.
        Directory.CreateDirectory(Path.Combine(_repository.Path, "src"));

        using var provider = CreateProvider();
        var watcher = (HookWatcher)provider.GetRequiredService<IHookWatcher>();

        using var cts = new CancellationTokenSource();
        var watchTask = watcher.WatchAsync(
            _repository.Path,
            new HookWatchOptions(Debounce: Debounce, Initial: true, Notify: false),
            progress: null,
            cts.Token);

        var initialCount = await WaitForRunCountAsync(1);
        Assert.True(initialCount >= 1, "the --initial run should have executed immediately");

        await WaitUntilWatchingAsync(watcher);

        WriteWatchedFile("a.txt");
        var afterWrite = await WaitForRunCountAsync(2);

        Assert.True(afterWrite >= 2, "a write in a pre-existing directory should add a second run after --initial");

        await Task.Delay(QuietWindow, TestContext.Current.CancellationToken);
        Assert.Equal(2, CountRuns());

        await StopAsync(watchTask, cts);
    }

    [Fact]
    public async Task Watch_Cancellation_StopsCleanly()
    {
        using var provider = CreateProvider();
        var watcher = (HookWatcher)provider.GetRequiredService<IHookWatcher>();

        using var cts = new CancellationTokenSource();
        var watchTask = watcher.WatchAsync(
            _repository.Path,
            new HookWatchOptions(Debounce: Debounce, Notify: false),
            progress: null,
            cts.Token);

        await WaitUntilWatchingAsync(watcher);

        cts.Cancel();
        var completed = await Task.WhenAny(watchTask, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Same(watchTask, completed);
        await watchTask;
        Assert.False(watcher.IsWatching);
    }

    // ---------------------------------------------------------------------- harness

    private ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddEngine(new EngineOptions { DatabasePath = _database.DatabasePath });
        services.AddSingleton(new HookPaths(_lockRoot));

        var provider = services.BuildServiceProvider();
        provider.InitializeEngineAsync().GetAwaiter().GetResult();
        return provider;
    }

    private void WriteWatchedFile(string name)
    {
        var directory = Path.Combine(_repository.Path, "src");

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(Path.Combine(directory, name), "content");
    }

    private async Task WaitUntilWatchingAsync(HookWatcher watcher)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(PollTimeoutMs);

        while (!watcher.IsWatching && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(watcher.IsWatching, "the watcher should arm within the timeout");
    }

    private async Task<int> WaitForRunCountAsync(int expected)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(PollTimeoutMs);
        var count = 0;

        while (DateTime.UtcNow < deadline)
        {
            count = CountRuns();

            if (count >= expected)
            {
                return count;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return count;
    }

    private int CountRuns()
    {
        using var context = _database.CreateContext();
        return context.Runs.Count(run => run.PipelineFileName == "build.yaml");
    }

    private static async Task StopAsync(Task watchTask, CancellationTokenSource cts)
    {
        cts.Cancel();
        var completed = await Task.WhenAny(watchTask, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Same(watchTask, completed);
        await watchTask;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }
}
