using Railway.Engine.Domain;
using Railway.Engine.Orchestration;
using Railway.Engine.Services;
using Railway.Engine.Services.Interfaces;

using FakeItEasy;

using Microsoft.EntityFrameworkCore;

using Tests.Infrastructure;

using Xunit;

namespace Tests.Unit;

/// <summary>
/// Unit tests for run provenance: the <see cref="RunTrigger"/> record, the
/// <see cref="RunRequest"/> defaults, the <see cref="RunStatus.Skipped"/> value and
/// the flow of <c>Trigger</c>/<c>TriggerDetail</c> through
/// <see cref="PipelineRunner"/> → <see cref="RunRequest"/> → <see cref="RunnerService"/> →
/// the persisted <see cref="Run"/> row.
/// <para>
/// The <see cref="PipelineRunner"/> tests use explicit start paths (no cwd mutation)
/// and fakes for discovery/validation, so they are safe to run in parallel. The
/// <see cref="RunnerService"/> test runs a pipeline with no jobs, so no process is spawned.
/// </para>
/// </summary>
public class RunProvenanceTests
{
    private const string ValidPipelineYaml = """
        name: build-pipeline
        jobs:
          build:
            steps:
              - name: compile
                run: dotnet build
        """;

    [Fact]
    public void RunRequest_DefaultsToManualTrigger()
    {
        var request = new RunRequest
        {
            RepositoryPath = "/repo",
            PipelineFileName = "build.yaml",
            Pipeline = new Pipeline { Name = "build" }
        };

        Assert.Equal("manual", request.Trigger);
        Assert.Null(request.TriggerDetail);
    }

    [Fact]
    public void RunTrigger_ManualIsTheManualKey()
    {
        Assert.Equal("manual", RunTrigger.Manual.Key);
        Assert.Null(RunTrigger.Manual.Detail);
    }

    [Fact]
    public void RunStatus_SkippedIsAppendedWithoutShiftingPersistedValues()
    {
        Assert.Equal(0, (int)RunStatus.Succeeded);
        Assert.Equal(1, (int)RunStatus.Failed);
        Assert.Equal(2, (int)RunStatus.Running);
        Assert.Equal(3, (int)RunStatus.Cancelled);
        Assert.Equal(4, (int)RunStatus.Skipped);

        RunStatus[] expected =
            [RunStatus.Succeeded, RunStatus.Failed, RunStatus.Running, RunStatus.Cancelled, RunStatus.Skipped];
        Assert.Equal(expected, Enum.GetValues<RunStatus>());
    }

    [Fact]
    public async Task RunnerServiceRun_PersistsTriggerProvenance()
    {
        using var database = new TestDatabaseFixture();
        using var repositoryDirectory = new TempDirectory();
        await using var context = database.CreateContext();
        var service = new RunnerService(context);

        var request = new RunRequest
        {
            RepositoryPath = repositoryDirectory.Path,
            PipelineFileName = "build.yaml",
            Pipeline = new Pipeline
            {
                Name = "build-pipeline",
                Variables = [],
                Jobs = []
            },
            Trigger = "hook",
            TriggerDetail = "post-commit sha=abc1234"
        };

        var run = await service.Run(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("hook", run.Trigger);
        Assert.Equal("post-commit sha=abc1234", run.TriggerDetail);

        await using var verify = database.CreateContext();
        var persisted = await verify.Runs.SingleAsync(
            r => r.Id == run.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal("hook", persisted.Trigger);
        Assert.Equal("post-commit sha=abc1234", persisted.TriggerDetail);
    }

    [Fact]
    public async Task RunnerServiceRun_DefaultsTriggerToManual_WhenRequestDoesNotSpecifyOne()
    {
        using var database = new TestDatabaseFixture();
        using var repositoryDirectory = new TempDirectory();
        await using var context = database.CreateContext();
        var service = new RunnerService(context);

        var request = new RunRequest
        {
            RepositoryPath = repositoryDirectory.Path,
            PipelineFileName = "build.yaml",
            Pipeline = new Pipeline
            {
                Name = "build-pipeline",
                Variables = [],
                Jobs = []
            }
        };

        var run = await service.Run(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("manual", run.Trigger);
        Assert.Null(run.TriggerDetail);
    }

    [Fact]
    public async Task PipelineRunner_WithoutTrigger_FlowsManualProvenance()
    {
        using var repo = new TempRepository(ValidPipelineYaml);

        var (runner, captured) = CreateRunner(repo);

        await runner.RunAsync(repo.Path, "build.yaml", verbose: false, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured.Request);
        Assert.Equal("manual", captured.Request!.Trigger);
        Assert.Null(captured.Request.TriggerDetail);
    }

    [Fact]
    public async Task PipelineRunner_WithTrigger_FlowsProvidedProvenance()
    {
        using var repo = new TempRepository(ValidPipelineYaml);

        var (runner, captured) = CreateRunner(repo);

        await runner.RunAsync(
            repo.Path,
            "build.yaml",
            verbose: false,
            ct: TestContext.Current.CancellationToken,
            trigger: new RunTrigger("schedule", "0 3 * * *"));

        Assert.NotNull(captured.Request);
        Assert.Equal("schedule", captured.Request!.Trigger);
        Assert.Equal("0 3 * * *", captured.Request.TriggerDetail);
    }

    private static (PipelineRunner Runner, CapturedRunRequest Captured) CreateRunner(TempRepository repo)
    {
        var captured = new CapturedRunRequest();

        var runnerService = A.Fake<IRunnerService>();
        A.CallTo(() => runnerService.Run(A<RunRequest>._, A<IProgress<RunEvent>>._, A<CancellationToken>._))
            .ReturnsLazily((RunRequest request, IProgress<RunEvent>? _, CancellationToken _) =>
            {
                captured.Request = request;
                return new Run { Id = 1, Status = RunStatus.Running };
            });

        var discovery = A.Fake<IPipelineDiscoveryService>();
        IReadOnlyList<DiscoveredPipeline> discovered =
            [new DiscoveredPipeline { FileName = "build.yaml", Path = repo.PipelinePath }];
        A.CallTo(() => discovery.Discover(A<string>._)).Returns(discovered);

        var validate = A.Fake<IValidateService>();
        A.CallTo(() => validate.IsValid(A<Pipeline>._)).Returns(Array.Empty<string>());

        return (new PipelineRunner(runnerService, discovery, validate), captured);
    }

    private sealed class CapturedRunRequest
    {
        public RunRequest? Request { get; set; }
    }

    /// <summary>A temp git/railway repo on disk with one pipeline file (explicit paths, no cwd mutation).</summary>
    private sealed class TempRepository : IDisposable
    {
        public TempRepository(string pipelineYaml)
        {
            Path = Directory.CreateTempSubdirectory("railway_provenance_").FullName;
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".railway"));

            PipelinePath = System.IO.Path.Combine(Path, ".railway", "build.yaml");
            System.IO.File.WriteAllText(PipelinePath, pipelineYaml);
        }

        public string Path { get; }

        public string PipelinePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
