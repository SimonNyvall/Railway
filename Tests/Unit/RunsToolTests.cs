using ModelContextProtocol;
using Railway.Engine.Domain;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Mcp.Tools;
using FakeItEasy;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Unit;

/// <summary>
/// Unit tests for <see cref="RunsTool"/> driven by a FakeItEasy fake of
/// <see cref="IRunHistoryQuery"/>: asserts the list and detail modes, DTO mapping,
/// path/limit forwarding, and error mapping.
/// </summary>
public class RunsToolTests
{
    [Fact]
    public async Task QueryAsync_ListMode_ReturnsRunSummaries()
    {
        using var repository = new TempDirectory();
        IReadOnlyList<Run> runs = [RunTestData.Run(2), RunTestData.Run(1)];
        var query = A.Fake<IRunHistoryQuery>();
        A.CallTo(() => query.ListRunsAsync(repository.Path, 5, A<CancellationToken>._)).Returns(runs);

        var tool = new RunsTool(query);

        var result = await tool.QueryAsync(limit: 5, runId: null, path: repository.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Run);
        Assert.Equal(2, result.Runs.Count);
        Assert.Equal(2, result.Runs[0].RunId);
        Assert.Equal("build.yaml", result.Runs[0].PipelineFileName);
        Assert.Equal("build-pipeline", result.Runs[0].PipelineName);
        Assert.Equal("Succeeded", result.Runs[0].Status);
        Assert.Equal(0, result.Runs[0].ExitCode);
        A.CallTo(() => query.ListRunsAsync(repository.Path, 5, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task QueryAsync_DefaultLimit_ForwardsFifty()
    {
        using var repository = new TempDirectory();
        var query = A.Fake<IRunHistoryQuery>();
        A.CallTo(() => query.ListRunsAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .Returns(Array.Empty<Run>());

        var tool = new RunsTool(query);

        var result = await tool.QueryAsync(path: repository.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Runs);
        Assert.Null(result.Run);
        A.CallTo(() => query.ListRunsAsync(repository.Path, 50, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task QueryAsync_DetailMode_ReturnsRunDetailWithoutListing()
    {
        using var repository = new TempDirectory();
        var detailed = RunTestData.DetailedRun(4);

        var query = A.Fake<IRunHistoryQuery>();
        A.CallTo(() => query.GetRunAsync(repository.Path, 4, A<CancellationToken>._)).Returns(detailed);

        var tool = new RunsTool(query);

        var result = await tool.QueryAsync(runId: 4, path: repository.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Runs);
        Assert.NotNull(result.Run);
        Assert.Equal(4, result.Run!.RunId);
        Assert.Equal("build-pipeline", result.Run.PipelineName);

        var jobResult = Assert.Single(result.Run.Jobs);
        Assert.Equal("build", jobResult.Name);
        Assert.Equal("ok", Assert.Single(jobResult.Steps).Output);

        A.CallTo(() => query.ListRunsAsync(A<string>._, A<int>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task QueryAsync_DetailDomainException_ThrowsMcpExceptionWithFriendlyMessage()
    {
        using var repository = new TempDirectory();
        var query = A.Fake<IRunHistoryQuery>();
        A.CallTo(() => query.GetRunAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .ThrowsAsync(new ArgumentException("Run 99 was not found."));

        var tool = new RunsTool(query);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tool.QueryAsync(runId: 99, path: repository.Path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("Run 99 was not found.", exception.Message);
    }

    [Fact]
    public async Task QueryAsync_ListDomainException_ThrowsMcpExceptionWithFriendlyMessage()
    {
        using var repository = new TempDirectory();
        var query = A.Fake<IRunHistoryQuery>();
        A.CallTo(() => query.ListRunsAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .ThrowsAsync(new NotARailwayRepositoryException());

        var tool = new RunsTool(query);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tool.QueryAsync(path: repository.Path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains($"No '.railway' directory was found under '{repository.Path}'", exception.Message);
    }
}
