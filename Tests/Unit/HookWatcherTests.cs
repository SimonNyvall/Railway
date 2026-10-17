using System.Collections.Concurrent;

using Cli.Handlers;
using Railway.Engine.Domain;
using Railway.Engine.Infrastructure;
using Railway.Engine.Orchestration;
using Railway.Engine.Orchestration.Interfaces;

using Tests.Infrastructure;

using Xunit;

namespace Tests.Unit;

/// <summary>
/// Unit tests for <see cref="HookWatcher"/>: glob→regex matching, the always-on ignore set
/// (segments, gitignored paths, directory events), per-pipeline debounce coalescing, the
/// <c>--initial</c> run, the <c>--pipeline</c> filter, pipelines without a watch trigger, and prompt
/// cancellation. Events are driven through a fake <see cref="IWatchEventSource"/> so no real file
/// watcher is ever started.
/// </summary>
public sealed class HookWatcherTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(60);

    private readonly TempGitRepo _repository = new(initGit: false);

    public void Dispose() => _repository.Dispose();

    // ---------------------------------------------------------------------- glob matching

    [Theory]
    [InlineData("**/*", "README.md", true)]
    [InlineData("**/*", "src/a.cs", true)]
    [InlineData("**/*", "src/nested/deep/a.cs", true)]
    [InlineData("src/**/*.cs", "src/a.cs", true)]
    [InlineData("src/**/*.cs", "src/nested/a.cs", true)]
    [InlineData("src/**/*.cs", "src/nested/deep/a.cs", true)]
    [InlineData("src/**/*.cs", "tests/a.cs", false)]
    [InlineData("src/**/*.cs", "src/a.txt", false)]
    [InlineData("*.cs", "a.cs", true)]
    [InlineData("*.cs", "src/a.cs", false)]
    [InlineData("?.cs", "a.cs", true)]
    [InlineData("?.cs", "ab.cs", false)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "a/c", false)]
    [InlineData("src/*.cs", "src/a.cs", true)]
    [InlineData("src/*.cs", "src/nested/a.cs", false)]
    [InlineData("src/**", "src/nested/a.cs", true)]
    [InlineData("[ab].cs", "a.cs", true)]
    [InlineData("[ab].cs", "c.cs", false)]
    [InlineData("[!ab].cs", "c.cs", true)]
    [InlineData("[!ab].cs", "a.cs", false)]
    public void MatchesGlob_Table(string pattern, string path, bool expected) =>
        Assert.Equal(expected, HookWatcher.MatchesGlob(pattern, path));

    [Fact]
    public void MatchesAnyGlob_TrueWhenAnyGlobMatches()
    {
        Assert.True(HookWatcher.MatchesAnyGlob(["*.md", "src/**/*.cs"], "src/a.cs"));
        Assert.False(HookWatcher.MatchesAnyGlob(["*.md", "src/**/*.cs"], "src/a.txt"));
    }

    // ---------------------------------------------------------------------- B2: glob robustness

    [Theory]
    [InlineData("[a-z]", "m", true)]
    [InlineData("[0-9].cs", "5.cs", true)]
    [InlineData("[0-9].cs", "x.cs", false)]
    public void MatchesGlob_ValidRange_IsPreserved(string pattern, string path, bool expected) =>
        Assert.Equal(expected, HookWatcher.MatchesGlob(pattern, path));

    [Theory]
    // Reversed range: the '-' degrades to a literal, so 'z', '-', and 'a' all match.
    [InlineData("[z-a]", "z", true)]
    [InlineData("[z-a]", "a", true)]
    [InlineData("[z-a]", "-", true)]
    [InlineData("[z-a]", "m", false)]
    // A lone backslash inside a class must not produce an invalid regex.
    [InlineData(@"[\]", @"\", true)]
    [InlineData(@"[\]", "x", false)]
    // Unterminated class: the '[' is a literal.
    [InlineData("[a-", "[a-", true)]
    [InlineData("[a-", "a", false)]
    // Empty class: treated as the literal '[' followed by the literal ']'.
    [InlineData("[]", "[]", true)]
    public void MatchesGlob_MalformedCharacterClass_DegradesWithoutThrowing(string pattern, string path, bool expected) =>
        Assert.Equal(expected, HookWatcher.MatchesGlob(pattern, path));

    [Fact]
    public void MatchesGlob_PathologicalPattern_FailsFastInsteadOfHanging()
    {
        // A glob of many `*a` segments against a non-matching run of 'a's is catastrophic for a
        // backtracking engine; the bounded timeout must turn it into a quick "no match".
        var pattern = string.Concat(Enumerable.Repeat("*a", 30)) + "b";
        var path = new string('a', 40);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var matched = HookWatcher.MatchesGlob(pattern, path);
        stopwatch.Stop();

        Assert.False(matched);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"pathological glob took {stopwatch.Elapsed}");
    }

    [Theory]
    [InlineData("file.cs", false)]
    [InlineData("src/file.cs", false)]
    [InlineData(".git/config", true)]
    [InlineData(".railway/pipelines/a.yaml", true)]
    [InlineData("bin/Debug/a.dll", true)]
    [InlineData("src/bin/a.dll", true)]
    [InlineData("obj/a.o", true)]
    [InlineData("node_modules/pkg/index.js", true)]
    [InlineData("web/node_modules/pkg/index.js", true)]
    public void IsInIgnoredDirectory_SegmentTable(string path, bool expected) =>
        Assert.Equal(expected, HookWatcher.IsInIgnoredDirectory(path));

    [Theory]
    [InlineData("/repo", "/repo/src/a.cs", "src/a.cs")]
    [InlineData("/repo", "/repo/sub/dir/a.cs", "sub/dir/a.cs")]
    public void TryGetRelativePosixPath_InsideRoot(string root, string full, string expected)
    {
        Assert.True(HookWatcher.TryGetRelativePosixPath(root, full, out var relative));
        Assert.Equal(expected, relative);
    }

    [Fact]
    public void TryGetRelativePosixPath_OutsideRoot_ReturnsFalse() =>
        Assert.False(HookWatcher.TryGetRelativePosixPath("/repo", "/other/a.cs", out _));

    // ---------------------------------------------------------------------- ignore decisions

    [Theory]
    [InlineData(".git/config")]
    [InlineData(".railway/pipelines/build.yaml")]
    [InlineData("bin/Debug/a.dll")]
    [InlineData("obj/a.o")]
    [InlineData("node_modules/pkg/index.js")]
    public async Task IgnoredDirectoryEvent_DoesNotRun(string relative)
    {
        await using var harness = await StartWatchAsync(WatchCandidate("build.yaml"));

        harness.Source.Raise(Path.Combine(_repository.Path, relative));

        Assert.False(await harness.Runner.TryWaitForRunAsync(QuietWindow(), TestContext.Current.CancellationToken));
        Assert.Empty(harness.Runner.Runs);
    }

    [Fact]
    public async Task DirectoryEvent_DoesNotRun()
    {
        await using var harness = await StartWatchAsync(WatchCandidate("build.yaml"));

        harness.Source.Raise(Path.Combine(_repository.Path, "src"), isDirectory: true);

        Assert.False(await harness.Runner.TryWaitForRunAsync(QuietWindow(), TestContext.Current.CancellationToken));
        Assert.Empty(harness.Runner.Runs);
    }

    [Fact]
    public async Task GitIgnoredEvent_DoesNotRun()
    {
        await using var harness = await StartWatchAsync(WatchCandidate("build.yaml"), gitIgnored: true);

        harness.Source.Raise(Path.Combine(_repository.Path, "secret.txt"));

        Assert.False(await harness.Runner.TryWaitForRunAsync(QuietWindow(), TestContext.Current.CancellationToken));
        Assert.Empty(harness.Runner.Runs);
    }

    [Fact]
    public async Task NotGitIgnoredEvent_Runs()
    {
        await using var harness = await StartWatchAsync(WatchCandidate("build.yaml"), gitIgnored: false);

        harness.Source.Raise(Path.Combine(_repository.Path, "src", "a.cs"));

        await harness.Runner.WaitForRunCountAsync(1, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(harness.Runner.Runs);
    }

    [Fact]
    public async Task NonMatchingGlobEvent_DoesNotRun()
    {
        await using var harness = await StartWatchAsync(WatchCandidate("build.yaml", "src/**/*.cs"));

        harness.Source.Raise(Path.Combine(_repository.Path, "README.md"));

        Assert.False(await harness.Runner.TryWaitForRunAsync(QuietWindow(), TestContext.Current.CancellationToken));
        Assert.Empty(harness.Runner.Runs);
    }

    [Fact]
    public async Task GitUnavailable_DegradesToNotIgnored()
    {
        await using var harness = await StartWatchAsync(
            WatchCandidate("build.yaml"),
            gitExitCode: SystemCommandResult.ExecutableNotFoundExitCode);

        harness.Source.Raise(Path.Combine(_repository.Path, "src", "a.cs"));

        await harness.Runner.WaitForRunCountAsync(1, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(harness.Runner.Runs);
    }

    // ---------------------------------------------------------------------- debounce / initial / filter

    [Fact]
    public async Task BurstOfEvents_CoalescesToExactlyOneRun()
    {
        await using var harness = await StartWatchAsync(WatchCandidate("build.yaml"));

        for (var i = 0; i < 8; i++)
        {
            harness.Source.Raise(Path.Combine(_repository.Path, "src", $"file{i}.cs"));
        }

        await harness.Runner.WaitForRunCountAsync(1, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // No second run arrives once the debounce window has fully elapsed.
        Assert.False(await harness.Runner.TryWaitForRunAsync(QuietWindow(), TestContext.Current.CancellationToken));
        Assert.Single(harness.Runner.Runs);
        Assert.Equal("build.yaml", harness.Runner.Runs.Single().PipelineFile);
        Assert.Equal(TriggerEvent.Watch, harness.Runner.Runs.Single().TriggerEvent);
    }

    [Fact]
    public async Task EventsSeparatedByMoreThanWindow_ProduceTwoRuns()
    {
        await using var harness = await StartWatchAsync(WatchCandidate("build.yaml"));

        harness.Source.Raise(Path.Combine(_repository.Path, "src", "a.cs"));
        await harness.Runner.WaitForRunCountAsync(1, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The window elapses with no further events -> no extra run (proves the separation).
        Assert.False(await harness.Runner.TryWaitForRunAsync(Debounce * 2, TestContext.Current.CancellationToken));

        harness.Source.Raise(Path.Combine(_repository.Path, "src", "b.cs"));
        await harness.Runner.WaitForRunCountAsync(2, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, harness.Runner.Runs.Count);
    }

    [Fact]
    public async Task Initial_RunsOnceImmediately_ThenOnceForTheBurst()
    {
        await using var harness = await StartWatchAsync(WatchCandidate("build.yaml"), initial: true);

        // The initial run happens before the watcher is armed.
        await harness.Runner.WaitForRunCountAsync(1, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        harness.Source.Raise(Path.Combine(_repository.Path, "src", "a.cs"));
        await harness.Runner.WaitForRunCountAsync(2, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, harness.Runner.Runs.Count);
    }

    [Fact]
    public async Task PipelineFilter_RestrictsRunsToTheNamedPipeline()
    {
        var source = new FakePipelineSource(
            WatchCandidate("a.yaml"),
            WatchCandidate("b.yaml"));

        await using var harness = await StartWatchAsync(source, pipeline: "b.yaml");

        harness.Source.Raise(Path.Combine(_repository.Path, "src", "a.cs"));
        await harness.Runner.WaitForRunCountAsync(1, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("b.yaml", harness.Runner.Runs.Single().PipelineFile);
    }

    [Fact]
    public async Task PipelineWithoutWatchTrigger_IsNeverRun()
    {
        var source = new FakePipelineSource(
            new HookPipelineCandidate(
                "commit.yaml",
                "/pipelines/commit.yaml",
                new Pipeline
                {
                    Name = "commit",
                    Triggers = [new TriggerDefinition { Event = TriggerEvent.PostCommit }],
                },
                []));

        await using var harness = await StartWatchAsync(source);

        harness.Source.Raise(Path.Combine(_repository.Path, "src", "a.cs"));

        Assert.False(await harness.Runner.TryWaitForRunAsync(QuietWindow(), TestContext.Current.CancellationToken));
        Assert.Empty(harness.Runner.Runs);
    }

    // ---------------------------------------------------------------------- B1/B2: robustness

    [Fact]
    public async Task ProgressSinkThatThrows_DoesNotKillTheLoop()
    {
        static HookRunOutcome FailingOutcome(string? file) =>
            new([new HookRunResult(
                file ?? "build.yaml",
                null,
                RunStatus.Failed,
                Skipped: false,
                Error: "[dim]boom [ unterminated")]);

        await using var harness = await StartWatchAsync(
            WatchCandidate("build.yaml"),
            progress: new ThrowingOnMarkupProgress(),
            outcomeFactory: FailingOutcome);

        // The first run reports a result whose markup makes the sink throw; the loop must survive.
        harness.Source.Raise(Path.Combine(_repository.Path, "src", "a.cs"));
        await harness.Runner.WaitForRunCountAsync(1, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // A second event proves the watch loop is still alive after the sink failure.
        harness.Source.Raise(Path.Combine(_repository.Path, "src", "b.cs"));
        await harness.Runner.WaitForRunCountAsync(2, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(harness.WatchTask.IsFaulted);
        Assert.Equal(2, harness.Runner.Runs.Count);
    }

    [Fact]
    public async Task PathologicalGlob_DoesNotMatchAndWarns()
    {
        var warnings = new List<string>();
        var pattern = string.Concat(Enumerable.Repeat("*a", 30)) + "b";

        await using var harness = await StartWatchAsync(
            WatchCandidate("build.yaml", pattern),
            warn: warnings.Add);

        harness.Source.Raise(Path.Combine(_repository.Path, new string('a', 40)));

        Assert.False(await harness.Runner.TryWaitForRunAsync(QuietWindow(), TestContext.Current.CancellationToken));
        Assert.Empty(harness.Runner.Runs);
        Assert.Contains(warnings, warning => warning.Contains("took too long"));
    }

    // ---------------------------------------------------------------------- cancellation

    [Fact]
    public async Task Cancellation_StopsTheLoopPromptly()
    {
        var harness = await StartWatchAsync(WatchCandidate("build.yaml"));

        var completed = await harness.StopAsync(TimeSpan.FromSeconds(5));

        Assert.True(completed, "WatchAsync should complete promptly after cancellation");
        Assert.True(harness.Source.Stopped);
        Assert.False(harness.Watcher.IsWatching);

        await harness.DisposeAsync();
    }

    [Fact]
    public async Task WatchAsync_NotARailwayRepository_Throws()
    {
        using var empty = new TempGitRepo(initGit: false, createRailway: false);
        var eventSource = new FakeWatchEventSource();
        var watcher = new HookWatcher(new FakePipelineSource(), new RecordingHookRunner(), NotIgnoringCommandRunner(), eventSource);

        await Assert.ThrowsAsync<NotARailwayRepositoryException>(() =>
            watcher.WatchAsync(
                empty.Path,
                new HookWatchOptions(),
                progress: null,
                CancellationToken.None));
    }

    // ---------------------------------------------------------------------- harness

    private static TimeSpan QuietWindow() => Debounce * 3;

    private static HookPipelineCandidate WatchCandidate(string fileName, params string[] globs) =>
        new(
            fileName,
            "/pipelines/" + fileName,
            new Pipeline
            {
                Name = Path.GetFileNameWithoutExtension(fileName),
                Triggers = [new TriggerDefinition { Event = TriggerEvent.Watch, WatchPaths = globs }],
            },
            []);

    private static FakeSystemCommandRunner NotIgnoringCommandRunner() =>
        new() { RunHandler = (_, _, _) => new SystemCommandResult(1, string.Empty, string.Empty) };

    private async Task<Harness> StartWatchAsync(
        HookPipelineCandidate candidate,
        string? pipeline = null,
        bool initial = false,
        bool? gitIgnored = null,
        int? gitExitCode = null,
        IProgress<HookRunResult>? progress = null,
        Action<string>? warn = null,
        Func<string?, HookRunOutcome>? outcomeFactory = null) =>
        await StartWatchAsync(
            new FakePipelineSource(candidate), pipeline, initial, gitIgnored, gitExitCode, progress, warn, outcomeFactory);

    private async Task<Harness> StartWatchAsync(
        IHookPipelineSource source,
        string? pipeline = null,
        bool initial = false,
        bool? gitIgnored = null,
        int? gitExitCode = null,
        IProgress<HookRunResult>? progress = null,
        Action<string>? warn = null,
        Func<string?, HookRunOutcome>? outcomeFactory = null)
    {
        var eventSource = new FakeWatchEventSource();
        var runner = new RecordingHookRunner { OutcomeFactory = outcomeFactory };

        var exitCode = gitExitCode ?? (gitIgnored == true ? 0 : 1);
        var commandRunner = new FakeSystemCommandRunner
        {
            RunHandler = (_, _, _) => new SystemCommandResult(exitCode, string.Empty, string.Empty),
        };

        var watcher = new HookWatcher(source, runner, commandRunner, eventSource, warn);
        var cts = new CancellationTokenSource();

        var watchTask = watcher.WatchAsync(
            _repository.Path,
            new HookWatchOptions(pipeline, Debounce, initial, Notify: false),
            progress,
            cts.Token);

        await eventSource.Started.WaitAsync(TimeSpan.FromSeconds(5));

        return new Harness(watcher, eventSource, runner, cts, watchTask);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(
            HookWatcher watcher,
            FakeWatchEventSource source,
            RecordingHookRunner runner,
            CancellationTokenSource cts,
            Task watchTask)
        {
            Watcher = watcher;
            Source = source;
            Runner = runner;
            Cts = cts;
            WatchTask = watchTask;
        }

        public HookWatcher Watcher { get; }

        public FakeWatchEventSource Source { get; }

        public RecordingHookRunner Runner { get; }

        public CancellationTokenSource Cts { get; }

        public Task WatchTask { get; }

        public async Task<bool> StopAsync(TimeSpan timeout)
        {
            Cts.Cancel();

            var finished = await Task.WhenAny(WatchTask, Task.Delay(timeout, TestContext.Current.CancellationToken));
            return finished == WatchTask;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync(TimeSpan.FromSeconds(5));
            Cts.Dispose();
        }
    }

    private sealed class FakeWatchEventSource : IWatchEventSource
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<WatchPathChange>? Changed;

        public Task Started => _started.Task;

        public bool Stopped { get; private set; }

        public string? StartedRoot { get; private set; }

        public void Start(string rootPath)
        {
            StartedRoot = rootPath;
            Stopped = false;
            _started.TrySetResult();
        }

        public void Stop() => Stopped = true;

        public void Dispose() => Stop();

        public void Raise(string fullPath, bool isDirectory = false) =>
            Changed?.Invoke(new WatchPathChange(fullPath, isDirectory));
    }

    private sealed class RecordingHookRunner : IHookRunner
    {
        private readonly SemaphoreSlim _calls = new(0);

        public ConcurrentQueue<RecordedRun> Runs { get; } = new();

        public Func<string?, HookRunOutcome>? OutcomeFactory { get; init; }

        public Task<HookRunOutcome> RunEventAsync(
            string repositoryPath,
            TriggerEvent triggerEvent,
            HookEventContext context,
            string? pipelineFile,
            bool detach,
            bool notify,
            CancellationToken ct)
        {
            Runs.Enqueue(new RecordedRun(repositoryPath, triggerEvent, context, pipelineFile));
            _calls.Release();

            var fileName = pipelineFile ?? "(all)";
            var outcome = OutcomeFactory?.Invoke(pipelineFile)
                ?? new HookRunOutcome(
                    [new HookRunResult(fileName, 1, RunStatus.Succeeded, Skipped: false, Error: null)]);

            return Task.FromResult(outcome);
        }

        public async Task WaitForRunCountAsync(
            int count,
            TimeSpan timeout,
            CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (Runs.Count < count)
            {
                var remaining = deadline - DateTime.UtcNow;

                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"Expected {count} run(s) within {timeout}, observed {Runs.Count}.");
                }

                await _calls.WaitAsync(remaining, ct);
            }
        }

        public async Task<bool> TryWaitForRunAsync(TimeSpan timeout, CancellationToken ct) =>
            await _calls.WaitAsync(timeout, ct);
    }

    private sealed record RecordedRun(
        string RepositoryPath,
        TriggerEvent TriggerEvent,
        HookEventContext Context,
        string? PipelineFile);

    /// <summary>Progress sink that throws like Spectre does on unbalanced markup.</summary>
    private sealed class ThrowingOnMarkupProgress : IProgress<HookRunResult>
    {
        public void Report(HookRunResult value)
        {
            if (value.Error?.Contains('[') == true)
            {
                throw new InvalidOperationException("Could not find color or style 'dim'.");
            }
        }
    }
}

/// <summary>
/// Regression for the unescaped-markup defect: a pipeline name or error containing an unbalanced
/// <c>[</c> must not crash <c>hooks watch</c>. Serialized with the <c>Integration Tests</c>
/// collection because the handler writes to the process-wide <see cref="Spectre.Console.AnsiConsole"/>.
/// </summary>
[Collection("Integration Tests")]
public sealed class HooksWatchHandlerEscapingTests
{
    [Fact]
    public async Task Handle_ResultWithUnbalancedMarkup_DoesNotThrow()
    {
        var console = TestConsoleFactory.Create();
        var reported = new HookRunResult(
            "[dim]bad.yaml",
            null,
            RunStatus.Failed,
            Skipped: false,
            Error: "[dim]unbalanced [ markup");
        var handler = new HooksWatchHandler(new ReportingWatcher(reported));

        await handler.Handle(
            pipeline: null,
            debounce: null,
            initial: false,
            path: null,
            verbose: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Contains("unbalanced", console.Output);
        Assert.Contains("[dim]bad.yaml", console.Output);
    }

    private sealed class ReportingWatcher : IHookWatcher
    {
        private readonly HookRunResult _result;

        public ReportingWatcher(HookRunResult result) => _result = result;

        public Task WatchAsync(
            string repositoryPath,
            HookWatchOptions options,
            IProgress<HookRunResult>? progress,
            CancellationToken ct)
        {
            progress?.Report(_result);
            return Task.CompletedTask;
        }
    }
}

