using Cli.Domain;
using Cli.Handlers;
using Cli.Services.Interfaces;
using FakeItEasy;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Unit;

/// <summary>
/// Unit tests for the real <see cref="RunHandler"/> driven by FakeItEasy fakes for
/// its <see cref="IRunnerService"/>, <see cref="IPipelineDiscoveryService"/> and
/// <see cref="IValidateService"/> dependencies.
/// <para>
/// <see cref="RunHandler"/> resolves the repository from the current working
/// directory via <see cref="Cli.Helpers.RepositoryHelper"/>, so these tests run
/// inside a temp git/railway repo (<see cref="TestRailwayRepository"/>) and are
/// serialized with the rest of the cwd/console-mutating suite through the
/// <c>Integration Tests</c> collection.
/// </para>
/// </summary>
[Collection("Integration Tests")]
public class RunHandlerTests : IDisposable
{
    private const string ValidPipelineYaml = """
        name: build-pipeline
        jobs:
          build:
            steps:
              - name: compile
                run: dotnet build
        """;

    private readonly TestRailwayRepository _repo;
    private readonly string _originalDir;

    public RunHandlerTests()
    {
        _originalDir = Directory.GetCurrentDirectory();
        _repo = new TestRailwayRepository();
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        _repo.Dispose();
    }

    private static IPipelineDiscoveryService DiscoveryReturning(params DiscoveredPipeline[] pipelines)
    {
        var discovery = A.Fake<IPipelineDiscoveryService>();
        IReadOnlyList<DiscoveredPipeline> discovered = pipelines;
        A.CallTo(() => discovery.Discover(A<string>._)).Returns(discovered);
        return discovery;
    }

    private DiscoveredPipeline WritePipeline(string fileName, string yaml)
    {
        _repo.WritePipeline(fileName, yaml);
        return new DiscoveredPipeline { FileName = fileName, Path = _repo.GetPipelinePath(fileName) };
    }

    private static IValidateService Validating(IEnumerable<string> errors)
    {
        var validate = A.Fake<IValidateService>();
        A.CallTo(() => validate.IsValid(A<Pipeline>._)).Returns(errors);
        return validate;
    }

    private static IRunnerService Running(Run run)
    {
        var runner = A.Fake<IRunnerService>();
        A.CallTo(() => runner.Run(A<RunRequest>._, A<CancellationToken>._)).Returns(run);
        return runner;
    }

    [Fact]
    public async Task Handle_PipelineNotFound_PrintsErrorAndThrows()
    {
        var console = TestConsoleFactory.Create();
        var handler = new RunHandler(A.Fake<IRunnerService>(), DiscoveryReturning(), A.Fake<IValidateService>());

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => handler.Handle("missing.yaml", verbose: false));

        Assert.Contains("Pipeline file not found", console.Output);
        Assert.Contains("missing.yaml", console.Output);
        Assert.Contains("missing.yaml", exception.Message);
    }

    [Fact]
    public async Task Handle_AmbiguousPipelineName_PrintsCandidatesAndThrows()
    {
        var discovery = DiscoveryReturning(
            new DiscoveredPipeline { FileName = "build.yaml", Path = "/repo/ci/build.yaml" },
            new DiscoveredPipeline { FileName = "build.yaml", Path = "/repo/other/build.yaml" });

        var console = TestConsoleFactory.Create();
        var handler = new RunHandler(A.Fake<IRunnerService>(), discovery, A.Fake<IValidateService>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle("build.yaml", verbose: false));

        Assert.Contains("Multiple pipeline files match", console.Output);
        Assert.Contains("/repo/ci/build.yaml", console.Output);
        Assert.Contains("/repo/other/build.yaml", console.Output);
        Assert.Contains("Ambiguous", exception.Message);
    }

    [Fact]
    public async Task Handle_UnparseablePipeline_PrintsParseErrorAndThrows()
    {
        var discovered = WritePipeline("build.yaml", "invalid: yaml: [");
        var console = TestConsoleFactory.Create();
        var handler = new RunHandler(A.Fake<IRunnerService>(), DiscoveryReturning(discovered), A.Fake<IValidateService>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle("build.yaml", verbose: false));

        Assert.Contains("Failed to parse pipeline", console.Output);
        Assert.Contains("Pipeline parse failed", exception.Message);
    }

    [Fact]
    public async Task Handle_InvalidPipeline_PrintsValidationErrorsAndDoesNotRun()
    {
        var discovered = WritePipeline("build.yaml", ValidPipelineYaml);
        var validate = Validating(["Job 'deploy' does not exist"]);
        var runner = A.Fake<IRunnerService>();

        var console = TestConsoleFactory.Create();
        var handler = new RunHandler(runner, DiscoveryReturning(discovered), validate);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle("build.yaml", verbose: false));

        Assert.Contains("Pipeline is invalid", console.Output);
        Assert.Contains("Job 'deploy' does not exist", console.Output);
        A.CallTo(() => runner.Run(A<RunRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Handle_SuccessfulRun_PrintsSuccessMessage()
    {
        var discovered = WritePipeline("build.yaml", ValidPipelineYaml);
        var runner = Running(new Run { Status = RunStatus.Succeeded });

        var console = TestConsoleFactory.Create();
        var handler = new RunHandler(runner, DiscoveryReturning(discovered), Validating([]));

        await handler.Handle("build.yaml", verbose: false);

        Assert.Contains("Pipeline completed successfully", console.Output);
    }

    [Fact]
    public async Task Handle_FailedRun_PrintsExitCodeAndThrows()
    {
        var discovered = WritePipeline("build.yaml", ValidPipelineYaml);
        var runner = Running(new Run { Status = RunStatus.Failed, ExitCode = 3 });

        var console = TestConsoleFactory.Create();
        var handler = new RunHandler(runner, DiscoveryReturning(discovered), Validating([]));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle("build.yaml", verbose: false));

        Assert.Contains("Pipeline failed with exit code 3", console.Output);
        Assert.Contains("exit code 3", exception.Message);
    }
}
