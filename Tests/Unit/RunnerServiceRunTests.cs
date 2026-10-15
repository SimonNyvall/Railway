using Railway.Engine.Domain;
using Railway.Engine.Services;
using Microsoft.EntityFrameworkCore;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Unit;

/// <summary>
/// End-to-end unit tests for the real <see cref="RunnerService.Run"/> method,
/// executing simple <c>sh -c</c> steps against a real SQLite database
/// (<see cref="TestDatabaseFixture"/>) and asserting the persisted run history.
/// <para>
/// Serialized with the <c>Integration Tests</c> collection because the service
/// spawns processes and writes to the process-wide <see cref="Spectre.Console.AnsiConsole"/>.
/// </para>
/// </summary>
[Collection("Integration Tests")]
public class RunnerServiceRunTests
{
    [Fact]
    public async Task Run_SimpleEchoStep_PersistsSucceededRun()
    {
        TestConsoleFactory.Create();
        using var database = new TestDatabaseFixture();
        using var repositoryDirectory = new TempDirectory();
        using var context = database.CreateContext();
        var service = new RunnerService(context);

        var request = new RunRequest
        {
            RepositoryPath = repositoryDirectory.Path,
            PipelineFileName = "build.yaml",
            Pipeline = new Pipeline
            {
                Name = "build-pipeline",
                Variables = [],
                Jobs = new Dictionary<string, Job>
                {
                    ["build"] = new Job
                    {
                        DependsOn = [],
                        Env = [],
                        Steps = [new Step { Name = "say", Run = "echo hello" }]
                    }
                }
            }
        };

        var run = await service.Run(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(0, run.ExitCode);
        Assert.NotNull(run.FinishedAt);

        using var verify = database.CreateContext();
        var persistedRun = await verify.Runs
            .Include(r => r.Jobs)
                .ThenInclude(j => j.Steps)
            .SingleAsync(r => r.Id == run.Id, TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.Succeeded, persistedRun.Status);
        Assert.Equal("build-pipeline", persistedRun.PipelineName);

        var job = Assert.Single(persistedRun.Jobs);
        Assert.Equal("build", job.Name);
        Assert.Equal(RunStatus.Succeeded, job.Status);

        var step = Assert.Single(job.Steps);
        Assert.Equal("say", step.Name);
        Assert.Equal("echo hello", step.Command);
        Assert.Equal(RunStatus.Succeeded, step.Status);
        Assert.Contains("hello", step.Output);
    }

    [Fact]
    public async Task Run_CircularDependency_ThrowsInvalidOperationException()
    {
        TestConsoleFactory.Create();
        using var database = new TestDatabaseFixture();
        using var repositoryDirectory = new TempDirectory();
        using var context = database.CreateContext();
        var service = new RunnerService(context);

        var request = new RunRequest
        {
            RepositoryPath = repositoryDirectory.Path,
            PipelineFileName = "circular.yaml",
            Pipeline = new Pipeline
            {
                Name = "circular-pipeline",
                Variables = [],
                Jobs = new Dictionary<string, Job>
                {
                    ["a"] = new Job { DependsOn = ["b"], Steps = [new Step { Run = "echo a" }] },
                    ["b"] = new Job { DependsOn = ["a"], Steps = [new Step { Run = "echo b" }] }
                }
            }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.Run(request, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Circular dependency", exception.Message);
    }
}
