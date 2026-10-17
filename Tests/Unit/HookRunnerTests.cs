using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Railway.Engine.Orchestration;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Engine.Services.Interfaces;

using Tests.Infrastructure;

using Xunit;

namespace Tests.Unit;

public sealed class HookRunnerTests : IDisposable
{
    private readonly TestDatabaseFixture _database = new();
    private readonly TempGitRepo _repository = new();
    private readonly string _lockRoot = Path.Combine(Path.GetTempPath(), "railway_hook_runner_locks_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _repository.Dispose();
        _database.Dispose();

        if (Directory.Exists(_lockRoot))
        {
            Directory.Delete(_lockRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunEventAsync_RunsOnlySubscribedPipelines_InSourceOrder()
    {
        var source = new FakePipelineSource(
            Candidate("push.yaml", TriggerEvent.PrePush),
            Candidate("commit.yaml", TriggerEvent.PostCommit),
            Candidate("merge.yaml", TriggerEvent.PostCommit));

        var runnerService = new FakeRunnerService();
        var runner = CreateRunner(source, runnerService, out _, out _, out _);

        var outcome = await runner.RunEventAsync(
            _repository.Path,
            TriggerEvent.PostCommit,
            new HookEventContext(Sha: "abc1234"),
            pipelineFile: null,
            detach: false,
            notify: false,
            CancellationToken.None);

        Assert.Equal(["commit.yaml", "merge.yaml"], runnerService.Order);
        Assert.Equal(["commit.yaml", "merge.yaml"], outcome.Results.Select(result => result.PipelineFileName));
        Assert.False(outcome.AnyFailed);
        Assert.False(outcome.AnySkipped);
    }

    [Fact]
    public async Task RunEventAsync_PipelineFileFilter_RestrictsToOnePipeline()
    {
        var source = new FakePipelineSource(
            Candidate("a.yaml", TriggerEvent.PostCommit),
            Candidate("b.yaml", TriggerEvent.PostCommit));

        var runnerService = new FakeRunnerService();
        var runner = CreateRunner(source, runnerService, out _, out _, out _);

        var outcome = await runner.RunEventAsync(
            _repository.Path,
            TriggerEvent.PostCommit,
            new HookEventContext(),
            pipelineFile: "b.yaml",
            detach: false,
            notify: false,
            CancellationToken.None);

        Assert.Equal(["b.yaml"], runnerService.Order);
        Assert.Single(outcome.Results);
    }

    [Fact]
    public async Task RunEventAsync_PipelineFileFilterThatMatchesNothing_ReturnsErrorResult()
    {
        var source = new FakePipelineSource(Candidate("a.yaml", TriggerEvent.PostCommit));
        var runner = CreateRunner(source, new FakeRunnerService(), out _, out _, out _);

        var outcome = await runner.RunEventAsync(
            _repository.Path,
            TriggerEvent.PostCommit,
            new HookEventContext(),
            pipelineFile: "missing.yaml",
            detach: false,
            notify: false,
            CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal("Pipeline 'missing.yaml' was not found.", result.Error);
        Assert.True(outcome.AnyFailed);
    }

    [Fact]
    public async Task RunEventAsync_PipelineExistsWithoutMatchingTrigger_ReportsNoMatchingTrigger()
    {
        var source = new FakePipelineSource(Candidate("a.yaml", TriggerEvent.PrePush));
        var runner = CreateRunner(source, new FakeRunnerService(), out _, out _, out _);

        var outcome = await runner.RunEventAsync(
            _repository.Path,
            TriggerEvent.PostCommit,
            new HookEventContext(),
            pipelineFile: "a.yaml",
            detach: false,
            notify: false,
            CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal("Pipeline 'a.yaml' has no matching trigger for event post-commit.", result.Error);
        Assert.True(outcome.AnyFailed);
    }

    [Fact]
    public async Task RunEventAsync_RecordsHookProvenanceWithContextDetail()
    {
        var source = new FakePipelineSource(Candidate("a.yaml", TriggerEvent.PostCommit));
        var runnerService = new FakeRunnerService();
        var runner = CreateRunner(source, runnerService, out _, out _, out _);

        await runner.RunEventAsync(
            _repository.Path,
            TriggerEvent.PostCommit,
            new HookEventContext(Branch: "main", Sha: "abc1234"),
            pipelineFile: null,
            detach: false,
            notify: false,
            CancellationToken.None);

        var request = Assert.Single(runnerService.Requests);
        Assert.Equal("hook", request.Trigger);
        Assert.Equal("branch=main, sha=abc1234", request.TriggerDetail);
    }

    [Fact]
    public async Task RunEventAsync_LockHeld_InsertsSkippedRunWithoutJobOrStepRows()
    {
        var source = new FakePipelineSource(Candidate("build.yaml", TriggerEvent.PostCommit));
        var runnerService = new FakeRunnerService();
        var runner = CreateRunner(source, runnerService, out var paths, out _, out _);

        using var held = HookLock.TryAcquire(
            paths.LockRoot,
            paths.GetRepositorySlug(_repository.Path),
            paths.GetPipelineSlug("build.yaml"));

        Assert.NotNull(held);

        var outcome = await runner.RunEventAsync(
            _repository.Path,
            TriggerEvent.PostCommit,
            new HookEventContext(Branch: "main"),
            pipelineFile: null,
            detach: false,
            notify: false,
            CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Skipped);
        Assert.Equal(RunStatus.Skipped, result.Status);
        Assert.NotNull(result.RunId);
        Assert.True(outcome.AnySkipped);
        Assert.Empty(runnerService.Requests);

        using var context = _database.CreateContext();
        var run = Assert.Single(context.Runs.ToList());
        Assert.Equal(RunStatus.Skipped, run.Status);
        Assert.Equal("hook", run.Trigger);
        Assert.Equal("branch=main", run.TriggerDetail);
        Assert.Equal(run.StartedAt, run.FinishedAt);
        Assert.Null(run.ExitCode);
        Assert.Empty(context.RunJobs.ToList());
        Assert.Empty(context.RunSteps.ToList());
    }

    [Fact]
    public async Task RunEventAsync_WithDetach_SpawnsOneDetachedChildPerPipelineAndDoesNotRunInProcess()
    {
        var source = new FakePipelineSource(
            Candidate("a.yaml", TriggerEvent.PostCommit),
            Candidate("b.yaml", TriggerEvent.PostCommit));

        var runnerService = new FakeRunnerService();
        var runner = CreateRunner(source, runnerService, out _, out var system, out _);

        var outcome = await runner.RunEventAsync(
            _repository.Path,
            TriggerEvent.PostCommit,
            new HookEventContext(),
            pipelineFile: null,
            detach: true,
            notify: false,
            CancellationToken.None);

        Assert.Empty(runnerService.Requests);
        Assert.All(outcome.Results, result => Assert.True(result.Detached));

        Assert.Equal(2, system.DetachedCalls.Count);

        var first = system.DetachedCalls[0];
        Assert.Equal("dotnet", first.FileName);
        Assert.Equal(
            [
                "/app/Cli.dll",
                "hooks", "run", "post-commit",
                "--path", _repository.Path,
                "--pipeline", "a.yaml",
                "--no-detach",
                "--no-notify",
            ],
            first.Arguments);
        Assert.Equal(_repository.Path, first.WorkingDirectory);
    }

    [Fact]
    public async Task RunEventAsync_WhenDetachSpawnFails_ReportsFailedResult()
    {
        var source = new FakePipelineSource(Candidate("a.yaml", TriggerEvent.PostCommit));
        var runner = CreateRunner(source, new FakeRunnerService(), out _, out _, out _, (_, _, _) => SystemCommandResult.NotFound("dotnet"));

        var outcome = await runner.RunEventAsync(
            _repository.Path,
            TriggerEvent.PostCommit,
            new HookEventContext(),
            pipelineFile: null,
            detach: true,
            notify: false,
            CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.True(outcome.AnyFailed);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task RunEventAsync_RunFailure_YieldsFailedResultAndNotifies()
    {
        var source = new FakePipelineSource(Candidate("a.yaml", TriggerEvent.PostCommit));
        var runnerService = new FakeRunnerService { Status = RunStatus.Failed };
        var runner = CreateRunner(source, runnerService, out _, out _, out var notifier);

        var outcome = await runner.RunEventAsync(
            _repository.Path,
            TriggerEvent.PostCommit,
            new HookEventContext(),
            pipelineFile: null,
            detach: false,
            notify: true,
            CancellationToken.None);

        Assert.True(outcome.AnyFailed);
        Assert.Single(notifier.Calls);
        Assert.Equal(NotificationLevel.OnFailure, notifier.Calls[0].Level);
    }

    [Fact]
    public async Task RunEventAsync_Success_DoesNotNotifyAtOnFailureLevel()
    {
        var source = new FakePipelineSource(Candidate("a.yaml", TriggerEvent.PostCommit));
        var runner = CreateRunner(source, new FakeRunnerService(), out _, out _, out var notifier);

        await runner.RunEventAsync(
            _repository.Path,
            TriggerEvent.PostCommit,
            new HookEventContext(),
            pipelineFile: null,
            detach: false,
            notify: true,
            CancellationToken.None);

        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task RunEventAsync_NotifyFalseAndNeverLevel_SuppressNotifications()
    {
        var neverSource = new FakePipelineSource(
            new HookPipelineCandidate(
                "a.yaml",
                "/pipelines/a.yaml",
                new Pipeline
                {
                    Name = "a",
                    Notify = NotificationLevel.Never,
                    Triggers = [new TriggerDefinition { Event = TriggerEvent.PostCommit }],
                },
                []));

        var runner = CreateRunner(neverSource, new FakeRunnerService { Status = RunStatus.Failed }, out _, out _, out var notifier);

        await runner.RunEventAsync(
            _repository.Path,
            TriggerEvent.PostCommit,
            new HookEventContext(),
            pipelineFile: null,
            detach: false,
            notify: true,
            CancellationToken.None);

        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task RunEventAsync_NotifierFailure_DoesNotBreakTheRun()
    {
        var source = new FakePipelineSource(Candidate("a.yaml", TriggerEvent.PostCommit));
        var runner = CreateRunner(
            source,
            new FakeRunnerService { Status = RunStatus.Failed },
            out _,
            out _,
            out var notifier);

        notifier.Throw = new InvalidOperationException("notifier down");

        var outcome = await runner.RunEventAsync(
            _repository.Path,
            TriggerEvent.PostCommit,
            new HookEventContext(),
            pipelineFile: null,
            detach: false,
            notify: true,
            CancellationToken.None);

        Assert.True(outcome.AnyFailed);
        Assert.Single(notifier.Calls);
    }

    [Fact]
    public async Task RunEventAsync_PipelineLevelFailure_ReturnsErrorInsteadOfThrowing()
    {
        var source = new FakePipelineSource(Candidate("a.yaml", TriggerEvent.PostCommit));
        var runner = CreateRunner(source, new FakeRunnerService { ThrowOnRun = new PipelineInvalidException("a.yaml", ["bad"], PipelineInvalidKind.Validation) }, out _, out _, out _);

        var outcome = await runner.RunEventAsync(
            _repository.Path,
            TriggerEvent.PostCommit,
            new HookEventContext(),
            pipelineFile: null,
            detach: false,
            notify: false,
            CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task RunEventAsync_UnparsableCandidate_ReturnsErrorResult()
    {
        var source = new FakePipelineSource(new HookPipelineCandidate("broken.yaml", "/pipelines/broken.yaml", null, ["bad yaml"]));
        var runner = CreateRunner(source, new FakeRunnerService(), out _, out _, out _);

        var outcome = await runner.RunEventAsync(
            _repository.Path,
            TriggerEvent.PostCommit,
            new HookEventContext(),
            pipelineFile: "broken.yaml",
            detach: false,
            notify: false,
            CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("bad yaml", result.Error);
    }

    [Fact]
    public async Task RunEventAsync_WritesPerRunLogUnderRailwayLogs()
    {
        var source = new FakePipelineSource(Candidate("build.yaml", TriggerEvent.PostCommit));
        var runner = CreateRunner(source, new FakeRunnerService(), out _, out _, out _);

        await runner.RunEventAsync(
            _repository.Path,
            TriggerEvent.PostCommit,
            new HookEventContext(),
            pipelineFile: null,
            detach: false,
            notify: false,
            CancellationToken.None);

        var logsDirectory = Path.Combine(_repository.Path, ".railway", "logs");
        var logFile = Assert.Single(Directory.GetFiles(logsDirectory));
        var content = File.ReadAllText(logFile);

        Assert.Contains("pipeline: build.yaml", content);
        Assert.Contains("event: post-commit", content);
        Assert.Contains("RunStarted", content);
    }

    [Fact]
    public async Task RunEventAsync_MissingRailwayDirectory_ThrowsDomainException()
    {
        var empty = new TempGitRepo(createRailway: false);
        var runner = CreateRunner(new FakePipelineSource(), new FakeRunnerService(), out _, out _, out _);

        try
        {
            await Assert.ThrowsAsync<NotARailwayRepositoryException>(() => runner.RunEventAsync(
                empty.Path,
                TriggerEvent.PostCommit,
                new HookEventContext(),
                pipelineFile: null,
                detach: false,
                notify: false,
                CancellationToken.None));
        }
        finally
        {
            empty.Dispose();
        }
    }

    [Theory]
    [InlineData("main", "v*", "main", null, true)]
    [InlineData("main", "v*", "dev", null, false)]
    [InlineData(null, "v*", "release", "v1.2.0", true)]
    [InlineData("main", null, "release", "v1.2.0", false)]
    [InlineData(null, null, null, null, true)]
    public void Selector_PushFiltersApplyBranchAndTagAndSemantics(
        string? branchFilter,
        string? tagFilter,
        string? branch,
        string? tag,
        bool expected)
    {
        var pipeline = new Pipeline
        {
            Name = "p",
            Triggers =
            [
                new TriggerDefinition
                {
                    Event = TriggerEvent.PrePush,
                    Branches = branchFilter is null ? [] : [branchFilter],
                    Tags = tagFilter is null ? [] : [tagFilter],
                },
            ],
        };

        var matched = HookPipelineSelector.MatchesEvent(
            pipeline,
            TriggerEvent.PrePush,
            new HookEventContext(Branch: branch, Tag: tag));

        Assert.Equal(expected, matched);
    }

    [Fact]
    public void Selector_NonPushEvents_IgnoreFilters()
    {
        var pipeline = new Pipeline
        {
            Name = "p",
            Triggers = [new TriggerDefinition { Event = TriggerEvent.PostCommit, Branches = ["main"] }],
        };

        Assert.True(HookPipelineSelector.MatchesEvent(pipeline, TriggerEvent.PostCommit, new HookEventContext()));
    }

    [Theory]
    [InlineData("v*", "v1.2", true)]
    [InlineData("v?", "v1", true)]
    [InlineData("v1.*", "v1.2", true)]
    [InlineData("main", "main", true)]
    [InlineData("main", "mainline", false)]
    [InlineData("v*", "release", false)]
    public void Selector_WildcardMatching(string pattern, string value, bool expected) =>
        Assert.Equal(expected, HookPipelineSelector.MatchesWildcard(pattern, value));

    [Fact]
    public void EventContext_BuildsAndTruncatesDetail()
    {
        Assert.Null(new HookEventContext().GetDetail());
        Assert.Equal("branch=main", new HookEventContext(Branch: "main").GetDetail());
        Assert.Equal(
            "tag=v1.2, remote=origin, is_squash=true",
            new HookEventContext(Tag: "v1.2", Remote: "origin", IsSquash: true).GetDetail());

        var longDetail = new HookEventContext(Note: new string('x', 500)).GetDetail();
        Assert.Equal(HookEventContext.MaxDetailLength, longDetail!.Length);
    }

    [Fact]
    public async Task PipelineHookSource_PreservesDiscoveryOrderAndSurfacesParseErrors()
    {
        var discovery = new FakeDiscoveryService(
            new DiscoveredPipeline { FileName = "b.yaml", Path = Path.Combine(_repository.RailwayPath, "pipelines", "b.yaml"), IsValid = true },
            new DiscoveredPipeline { FileName = "a.yaml", Path = Path.Combine(_repository.RailwayPath, "pipelines", "a.yaml"), IsValid = true },
            new DiscoveredPipeline { FileName = "bad.yaml", Path = "/nope/bad.yaml", IsValid = false, Errors = ["broken"] });

        _repository.WritePipeline("b.yaml", "name: b\ntriggers:\n  - post-commit\n");
        _repository.WritePipeline("a.yaml", "name: a\n");

        var source = new PipelineHookSource(discovery);
        var candidates = await source.GetPipelinesAsync(_repository.RailwayPath);

        Assert.Equal(["b.yaml", "a.yaml", "bad.yaml"], candidates.Select(candidate => candidate.FileName));
        Assert.Equal(TriggerEvent.PostCommit, Assert.Single(candidates[0].Pipeline!.Triggers).Event);
        Assert.Null(candidates[2].Pipeline);
        Assert.Contains("broken", candidates[2].Errors);
    }

    private static HookPipelineCandidate Candidate(string fileName, TriggerEvent triggerEvent) =>
        new(
            fileName,
            "/pipelines/" + fileName,
            new Pipeline
            {
                Name = Path.GetFileNameWithoutExtension(fileName),
                Triggers = [new TriggerDefinition { Event = triggerEvent }],
            },
            []);

    private HookRunner CreateRunner(
        FakePipelineSource source,
        FakeRunnerService runnerService,
        out HookPaths paths,
        out FakeSystemCommandRunner system,
        out RecordingNotifier notifier,
        Func<string, IReadOnlyList<string>, string?, SystemCommandResult>? detachHandler = null)
    {
        system = new FakeSystemCommandRunner { DetachHandler = detachHandler };

        var discovery = new FakeDiscoveryService([.. source.Candidates.Select(candidate => new DiscoveredPipeline
        {
            FileName = candidate.FileName,
            Path = Path.Combine(_repository.RailwayPath, "pipelines", candidate.FileName),
            IsValid = true,
        })]);

        var pipelineRunner = new PipelineRunner(runnerService, discovery, new FakeValidateService());

        foreach (var candidate in source.Candidates)
        {
            _repository.WritePipeline(candidate.FileName, $"name: {Path.GetFileNameWithoutExtension(candidate.FileName)}\n");
        }

        paths = new HookPaths(_lockRoot);
        notifier = new RecordingNotifier();

        return new HookRunner(
            source,
            pipelineRunner,
            paths,
            system,
            new FakeLauncher(),
            _database.CreateContext(),
            notifier);
    }

    private sealed class FakeRunnerService : IRunnerService
    {
        public List<RunRequest> Requests { get; } = [];

        public List<string> Order { get; } = [];

        public RunStatus Status { get; init; } = RunStatus.Succeeded;

        public Exception? ThrowOnRun { get; init; }

        public Task<Run> Run(RunRequest request, IProgress<RunEvent>? progress = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Order.Add(request.PipelineFileName);

            if (ThrowOnRun is not null)
            {
                throw ThrowOnRun;
            }

            var run = new Run
            {
                Id = Requests.Count,
                RepositoryId = 1,
                PipelineFileName = request.PipelineFileName,
                PipelineName = request.Pipeline.Name,
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow,
                Status = Status,
                Trigger = request.Trigger,
                TriggerDetail = request.TriggerDetail,
            };

            progress?.Report(new RunEvent
            {
                Kind = RunEventKind.RunStarted,
                RunId = run.Id,
                Timestamp = DateTimeOffset.UtcNow,
            });

            return Task.FromResult(run);
        }
    }
}

/// <summary>Shared test doubles for the hook test files.</summary>
internal sealed class FakePipelineSource : IHookPipelineSource
{
    private readonly IReadOnlyList<HookPipelineCandidate> _candidates;

    public FakePipelineSource(params HookPipelineCandidate[] candidates) => _candidates = candidates;

    public IReadOnlyList<HookPipelineCandidate> Candidates => _candidates;

    public Task<IReadOnlyList<HookPipelineCandidate>> GetPipelinesAsync(string railwayPath) =>
        Task.FromResult(_candidates);
}

internal sealed class FakeDiscoveryService : IPipelineDiscoveryService
{
    private readonly IReadOnlyList<DiscoveredPipeline> _pipelines;

    public FakeDiscoveryService(params DiscoveredPipeline[] pipelines) => _pipelines = pipelines;

    public FakeDiscoveryService(IReadOnlyList<DiscoveredPipeline> pipelines) => _pipelines = pipelines;

    public Task<IReadOnlyList<DiscoveredPipeline>> Discover(string railwayPath) => Task.FromResult(_pipelines);
}

internal sealed class FakeValidateService : IValidateService
{
    private readonly IReadOnlyList<string> _errors;

    public FakeValidateService(params string[] errors) => _errors = errors;

    public Task<IEnumerable<string>> IsValid(Pipeline pipeline) => Task.FromResult<IEnumerable<string>>(_errors);
}

internal sealed class FakeSystemCommandRunner : ISystemCommandRunner
{
    public List<(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory)> DetachedCalls { get; } = [];

    public List<(string FileName, IReadOnlyList<string> Arguments)> RunCalls { get; } = [];

    public Func<string, IReadOnlyList<string>, string?, SystemCommandResult>? RunHandler { get; init; }

    public Func<string, IReadOnlyList<string>, string?, SystemCommandResult>? DetachHandler { get; init; }

    public SystemCommandResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        string? stdin = null,
        int timeoutMs = 30_000)
    {
        RunCalls.Add((fileName, arguments));
        return RunHandler?.Invoke(fileName, arguments, workingDirectory)
            ?? new SystemCommandResult(0, string.Empty, string.Empty);
    }

    public SystemCommandResult RunDetached(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null)
    {
        DetachedCalls.Add((fileName, arguments, workingDirectory));
        return DetachHandler?.Invoke(fileName, arguments, workingDirectory)
            ?? new SystemCommandResult(0, string.Empty, string.Empty);
    }
}

internal sealed class FakeLauncher : IHookLauncher
{
    public HookLaunchCommand Command { get; set; } = new("dotnet", ["/app/Cli.dll"]);

    public HookLaunchCommand GetLaunchCommand() => Command;
}

internal sealed class RecordingNotifier : INotificationService
{
    public List<(string Title, string Message, NotificationLevel Level)> Calls { get; } = [];

    public Exception? Throw { get; set; }

    public Task NotifyAsync(string title, string message, NotificationLevel level)
    {
        Calls.Add((title, message, level));

        if (Throw is not null)
        {
            throw Throw;
        }

        return Task.CompletedTask;
    }
}

/// <summary>Temp git repository that never touches the process working directory.</summary>
internal sealed class TempGitRepo : IDisposable
{
    public TempGitRepo(bool initGit = true, bool createRailway = true)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "railway_hook_repo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);

        if (initGit)
        {
            RunGit("init");
        }

        if (createRailway)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".railway"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".railway", "pipelines"));
        }
    }

    public string Path { get; }

    public string RailwayPath => System.IO.Path.Combine(Path, ".railway");

    public string PipelinePath(string fileName) => System.IO.Path.Combine(RailwayPath, "pipelines", fileName);

    public void WritePipeline(string fileName, string yaml) => File.WriteAllText(PipelinePath(fileName), yaml);

    public (int ExitCode, string StandardOutput, string StandardError) RunGit(params string[] arguments)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, standardOutput, standardError);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
