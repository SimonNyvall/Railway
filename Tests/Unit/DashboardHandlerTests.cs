using Railway.Engine.Domain;
using Cli.Handlers;
using Railway.Engine.Orchestration;
using Railway.Engine.Services.Interfaces;
using FakeItEasy;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Unit;

/// <summary>
/// Unit tests for the real <see cref="DashboardHandler"/> driven by a FakeItEasy
/// fake for <see cref="IDashboardService"/> behind the real
/// <see cref="DashboardQuery"/> orchestrator.
/// <para>
/// Serialized with the <c>Integration Tests</c> collection because the handler
/// writes to the process-wide <see cref="Spectre.Console.AnsiConsole"/>.
/// </para>
/// </summary>
[Collection("Integration Tests")]
public class DashboardHandlerTests
{
    private static DashboardHandler CreateHandler(DashboardView view)
    {
        var dashboardService = A.Fake<IDashboardService>();
        A.CallTo(() => dashboardService.GetView(A<string>._)).Returns(view);
        return new DashboardHandler(new DashboardQuery(dashboardService));
    }

    [Fact]
    public async Task Handle_EmptyView_RendersRepositoryAndPlaceholders()
    {
        var console = TestConsoleFactory.Create();
        var handler = CreateHandler(new DashboardView { RepositoryName = "my-repo" });

        await handler.Handle();

        Assert.Contains("Repository: my-repo", console.Output);
        Assert.Contains("No pipelines configured", console.Output);
        Assert.Contains("No runs yet", console.Output);
    }

    [Fact]
    public async Task Handle_ValidAndInvalidPipelines_RendersValidationMarkup()
    {
        var view = new DashboardView
        {
            RepositoryName = "my-repo",
            Pipelines =
            [
                new DiscoveredPipeline { FileName = "build.yaml", Path = "/repo/build.yaml", Name = "build", IsValid = true },
                new DiscoveredPipeline
                {
                    FileName = "bad.yaml",
                    Path = "/repo/bad.yaml",
                    IsValid = false,
                    Errors = ["Job 'deploy' does not exist"]
                }
            ]
        };

        var console = TestConsoleFactory.Create();
        var handler = CreateHandler(view);

        await handler.Handle();

        Assert.Contains("build.yaml", console.Output);
        Assert.Contains("name: build", console.Output);
        Assert.Contains("bad.yaml", console.Output);
        Assert.Contains("(invalid)", console.Output);
        Assert.Contains("Job 'deploy' does not exist", console.Output);
    }

    [Fact]
    public async Task Handle_RecentRuns_RendersStatusMarkup()
    {
        var startedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var view = new DashboardView
        {
            RepositoryName = "my-repo",
            RecentRuns =
            [
                new Run
                {
                    PipelineFileName = "build.yaml",
                    PipelineName = "build",
                    Status = RunStatus.Succeeded,
                    ExitCode = 0,
                    StartedAt = startedAt
                },
                new Run
                {
                    PipelineFileName = "deploy.yaml",
                    PipelineName = "deploy",
                    Status = RunStatus.Failed,
                    ExitCode = 5,
                    StartedAt = startedAt
                },
                new Run
                {
                    PipelineFileName = "slow.yaml",
                    PipelineName = "slow",
                    Status = RunStatus.Running,
                    StartedAt = startedAt
                },
                new Run
                {
                    PipelineFileName = "stop.yaml",
                    PipelineName = "stop",
                    Status = RunStatus.Cancelled,
                    StartedAt = startedAt
                }
            ]
        };

        var console = TestConsoleFactory.Create();
        var handler = CreateHandler(view);

        await handler.Handle();

        Assert.Contains("Succeeded", console.Output);
        Assert.Contains("Failed (exit 5)", console.Output);
        Assert.Contains("Running", console.Output);
        Assert.Contains("Cancelled", console.Output);
        Assert.Contains("2026-01-02 03:04:05", console.Output);
    }
}
