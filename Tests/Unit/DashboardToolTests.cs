using ModelContextProtocol;
using Railway.Engine.Domain;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Mcp.Tools;
using FakeItEasy;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Unit;

/// <summary>
/// Unit tests for <see cref="DashboardTool"/> driven by a FakeItEasy fake of
/// <see cref="IDashboardQuery"/>: asserts DTO mapping, path forwarding, and error mapping.
/// </summary>
public class DashboardToolTests
{
    [Fact]
    public async Task QueryAsync_MapsViewToResult()
    {
        using var repository = new TempDirectory();
        var startedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var view = new DashboardView
        {
            RepositoryName = "my-repo",
            Pipelines =
            [
                new DiscoveredPipeline
                {
                    FileName = "build.yaml",
                    Path = Path.Combine(repository.Path, "build.yaml"),
                    Name = "build",
                    IsValid = true,
                },
            ],
            RecentRuns =
            [
                new Run
                {
                    Id = 9,
                    PipelineFileName = "build.yaml",
                    PipelineName = "build",
                    Status = RunStatus.Failed,
                    ExitCode = 1,
                    StartedAt = startedAt,
                    FinishedAt = startedAt.AddSeconds(2),
                },
            ],
        };

        var query = A.Fake<IDashboardQuery>();
        A.CallTo(() => query.QueryAsync(repository.Path, A<CancellationToken>._)).Returns(view);

        var tool = new DashboardTool(query);

        var result = await tool.QueryAsync(repository.Path, TestContext.Current.CancellationToken);

        Assert.Equal("my-repo", result.RepositoryName);

        var pipeline = Assert.Single(result.Pipelines);
        Assert.Equal("build.yaml", pipeline.FileName);
        Assert.Equal("build", pipeline.Name);
        Assert.True(pipeline.IsValid);

        var run = Assert.Single(result.RecentRuns);
        Assert.Equal(9, run.RunId);
        Assert.Equal("build.yaml", run.PipelineFileName);
        Assert.Equal("build", run.PipelineName);
        Assert.Equal("Failed", run.Status);
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(startedAt, run.StartedAt);
        Assert.Equal(startedAt.AddSeconds(2), run.FinishedAt);
    }

    [Fact]
    public async Task QueryAsync_ExplicitPath_IsForwardedToEngine()
    {
        using var repository = new TempDirectory();
        var query = A.Fake<IDashboardQuery>();
        A.CallTo(() => query.QueryAsync(A<string>._, A<CancellationToken>._))
            .Returns(new DashboardView { RepositoryName = "my-repo" });

        var tool = new DashboardTool(query);

        await tool.QueryAsync(repository.Path, TestContext.Current.CancellationToken);

        A.CallTo(() => query.QueryAsync(repository.Path, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task QueryAsync_DomainException_ThrowsMcpExceptionWithFriendlyMessage()
    {
        using var repository = new TempDirectory();
        var query = A.Fake<IDashboardQuery>();
        A.CallTo(() => query.QueryAsync(A<string>._, A<CancellationToken>._))
            .ThrowsAsync(new NotARailwayRepositoryException());

        var tool = new DashboardTool(query);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tool.QueryAsync(repository.Path, TestContext.Current.CancellationToken));

        Assert.Contains($"No '.railway' directory was found under '{repository.Path}'", exception.Message);
    }
}
