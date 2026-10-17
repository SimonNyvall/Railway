using Railway.Engine.Domain;
using Railway.Engine.Services;

using Tests.Infrastructure;

using Xunit;

namespace Tests.Unit;

/// <summary>
/// Cancellation behavior of the real <see cref="RunnerService"/>: cancelling a run (the CLI does this
/// on Ctrl+C) must terminate the running step's process tree instead of leaving an orphan behind
/// while the hook lock is released.
/// </summary>
public sealed class RunnerServiceCancellationTests : IDisposable
{
    private readonly TestDatabaseFixture _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Run_CancelledMidStep_KillsTheStepProcessTree()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses POSIX sh background pid capture");

        using var repository = new TempDirectory();
        var service = new RunnerService(_database.CreateContext());

        // The step's leaf is a grandchild: the shell waits for it, so only a tree kill (not just
        // killing the direct child) takes the workload down.
        var pipeline = new Pipeline
        {
            Name = "cancellable",
            Jobs =
            {
                ["job"] = new Job
                {
                    Steps =
                    [
                        new Step
                        {
                            Name = "long",
                            Run = $"{TestProcesses.LongRunningLeaf} & echo $! > \"{repository.File("pid")}\"; wait",
                        },
                    ],
                },
            },
        };

        var request = new RunRequest
        {
            RepositoryPath = repository.Path,
            PipelineFileName = "cancellable.yaml",
            Pipeline = pipeline,
        };

        using var cancellation = new CancellationTokenSource();
        var runTask = service.Run(request, progress: null, cancellation.Token);

        var leafId = await TestProcesses.ReadProcessIdAsync(repository.File("pid"), TimeSpan.FromSeconds(15));
        Assert.True(TestProcesses.IsRunning(leafId), "the step leaf should be running before cancellation");

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        Assert.True(
            await TestProcesses.WaitForProcessExitAsync(leafId, TimeSpan.FromSeconds(15)),
            "cancelling a run must terminate the step's process tree");
    }
}
