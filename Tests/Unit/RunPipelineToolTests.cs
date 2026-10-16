using ModelContextProtocol;
using Railway.Engine.Domain;
using Railway.Engine.Orchestration;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Mcp.Tools;
using FakeItEasy;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Unit;

/// <summary>
/// Unit tests for <see cref="RunPipelineTool"/> driven by FakeItEasy fakes of
/// <see cref="IPipelineRunner"/> and <see cref="IRunHistoryQuery"/>: asserts that the run
/// detail is loaded and mapped, the resolved path is forwarded, progress is wired through
/// the adapter, and domain exceptions become friendly <see cref="McpException"/>s.
/// </summary>
public class RunPipelineToolTests
{
    [Fact]
    public async Task RunAsync_LoadsAndMapsRunDetail()
    {
        using var repository = new TempDirectory();
        var runner = A.Fake<IPipelineRunner>();
        A.CallTo(() => runner.RunAsync(repository.Path, "build.yaml", false, A<IProgress<RunEvent>>._, A<CancellationToken>._))
            .Returns(new RunOutcome(new Run { Id = 42 }));
        var history = A.Fake<IRunHistoryQuery>();
        A.CallTo(() => history.GetRunAsync(repository.Path, 42, A<CancellationToken>._))
            .Returns(RunTestData.DetailedRun(42));

        var tool = new RunPipelineTool(runner, history);

        var result = await tool.RunAsync(
            "build.yaml",
            verbose: false,
            includeOutput: false,
            path: repository.Path,
            progress: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(42, result.RunId);
        Assert.Equal("build-pipeline", result.PipelineName);
        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(0, result.ExitCode);

        var job = Assert.Single(result.Jobs);
        Assert.Equal("build", job.Name);
        Assert.Equal("Succeeded", job.Status);
        Assert.Equal(0, job.ExitCode);

        var step = Assert.Single(job.Steps);
        Assert.Equal("compile", step.Name);
        Assert.Equal("dotnet build", step.Command);
        Assert.Equal("Succeeded", step.Status);
        Assert.Equal(0, step.ExitCode);
        Assert.Equal("ok", step.Output);

        A.CallTo(() => runner.RunAsync(repository.Path, "build.yaml", false, A<IProgress<RunEvent>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => history.GetRunAsync(repository.Path, 42, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_ExplicitPath_IsForwardedToRunner()
    {
        using var repository = new TempDirectory();
        var runner = A.Fake<IPipelineRunner>();
        A.CallTo(() => runner.RunAsync(A<string>._, A<string>._, A<bool>._, A<IProgress<RunEvent>>._, A<CancellationToken>._))
            .Returns(new RunOutcome(new Run { Id = 1 }));
        var history = A.Fake<IRunHistoryQuery>();
        A.CallTo(() => history.GetRunAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .Returns(RunTestData.DetailedRun(1));

        var tool = new RunPipelineTool(runner, history);

        await tool.RunAsync(
            "build.yaml",
            verbose: true,
            includeOutput: true,
            path: repository.Path,
            progress: null,
            cancellationToken: TestContext.Current.CancellationToken);

        A.CallTo(() => runner.RunAsync(repository.Path, "build.yaml", true, A<IProgress<RunEvent>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunAsync_ReportsProgressThroughAdapter()
    {
        using var repository = new TempDirectory();
        IProgress<RunEvent>? capturedProgress = null;
        var runner = A.Fake<IPipelineRunner>();
        A.CallTo(() => runner.RunAsync(A<string>._, A<string>._, A<bool>._, A<IProgress<RunEvent>>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, bool _, IProgress<RunEvent>? progress, CancellationToken _) =>
            {
                capturedProgress = progress;
                return new RunOutcome(new Run { Id = 7 });
            });
        var history = A.Fake<IRunHistoryQuery>();
        A.CallTo(() => history.GetRunAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .Returns(new Run { Id = 7, PipelineName = "p" });

        var sink = new RecordingProgress<ProgressNotificationValue>();
        var tool = new RunPipelineTool(runner, history);

        await tool.RunAsync(
            "build.yaml",
            path: repository.Path,
            progress: sink,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(capturedProgress);
        capturedProgress!.Report(new RunEvent { Kind = RunEventKind.RunStarted, RunId = 7 });

        Assert.Equal("Run 7 started.", Assert.Single(sink.Values).Message);
    }

    [Fact]
    public async Task RunAsync_RunnerDomainException_ThrowsMcpExceptionWithFriendlyMessage()
    {
        using var repository = new TempDirectory();
        var runner = A.Fake<IPipelineRunner>();
        A.CallTo(() => runner.RunAsync(A<string>._, A<string>._, A<bool>._, A<IProgress<RunEvent>>._, A<CancellationToken>._))
            .ThrowsAsync(new PipelineFileNotFoundException("missing.yaml"));

        var tool = new RunPipelineTool(runner, A.Fake<IRunHistoryQuery>());

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            tool.RunAsync("missing.yaml", path: repository.Path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Pipeline file 'missing.yaml' was not found", exception.Message);
    }

    [Fact]
    public async Task RunAsync_HistoryDomainException_ThrowsMcpExceptionWithFriendlyMessage()
    {
        using var repository = new TempDirectory();
        var runner = A.Fake<IPipelineRunner>();
        A.CallTo(() => runner.RunAsync(A<string>._, A<string>._, A<bool>._, A<IProgress<RunEvent>>._, A<CancellationToken>._))
            .Returns(new RunOutcome(new Run { Id = 3 }));
        var history = A.Fake<IRunHistoryQuery>();
        A.CallTo(() => history.GetRunAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .ThrowsAsync(new ArgumentException("Run 3 was not found."));

        var tool = new RunPipelineTool(runner, history);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            tool.RunAsync("build.yaml", path: repository.Path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("Run 3 was not found.", exception.Message);
    }
}
