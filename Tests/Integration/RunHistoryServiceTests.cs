using Railway.Engine.Domain;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Engine.Services.Interfaces;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Integration;

[Collection("Integration Tests")]
public class RunHistoryServiceTests : IDisposable
{
    private readonly TestRailwayRepository _repo;
    private readonly string _originalDir;

    public RunHistoryServiceTests()
    {
        _originalDir = Directory.GetCurrentDirectory();
        _repo = new TestRailwayRepository();
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        _repo.Dispose();
    }

    private void SetRepoDirectory()
    {
        Directory.SetCurrentDirectory(_repo.Path);
    }

    private static async Task<int> RunPipelineAsync(TestConsole console, string fileName)
    {
        var app = new CommandApp(new TestTypeRegistrar(console));
        app.Configure(c =>
        {
            c.AddCommand<Cli.Commands.InitCommand>("init");
            c.AddCommand<Cli.Commands.RunCommand>("run");
        });

        return await app.RunAsync(["run", fileName], TestContext.Current.CancellationToken);
    }

    private static (IRunHistoryQuery Query, IRunHistoryService Service) ResolveHistory()
    {
        var resolver = new TestTypeRegistrar(TestConsoleFactory.Create()).Build();
        return (
            (IRunHistoryQuery)resolver.Resolve(typeof(IRunHistoryQuery))!,
            (IRunHistoryService)resolver.Resolve(typeof(IRunHistoryService))!);
    }

    [Fact]
    public async Task RunHistoryQuery_ReturnsRunsPersistedByRealRun()
    {
        _repo.WritePipeline("build.yaml", """
            name: build-pipeline
            jobs:
              build:
                steps:
                  - name: echo
                    run: echo "Hello History"
            """);

        SetRepoDirectory();

        var runResult = await RunPipelineAsync(TestConsoleFactory.Create(), "build.yaml");
        Assert.Equal(0, runResult);

        var (query, service) = ResolveHistory();
        var repoPath = Directory.GetCurrentDirectory();

        var runs = await query.ListRunsAsync(repoPath, 10, TestContext.Current.CancellationToken);
        Assert.NotEmpty(runs);

        var firstRun = runs[0];
        Assert.Equal("build.yaml", firstRun.PipelineFileName);
        Assert.Equal("build-pipeline", firstRun.PipelineName);
        Assert.Equal(RunStatus.Succeeded, firstRun.Status);

        var detailedRun = await service.GetRunAsync(repoPath, firstRun.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(detailedRun);
        Assert.NotEmpty(detailedRun.Jobs);
        Assert.NotEmpty(detailedRun.Jobs.First().Steps);
    }

    [Fact]
    public async Task GetRunAsync_TruncatesStepOutputToBoundedLength()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses POSIX shell pipeline");

        _repo.WritePipeline("big.yaml", """
            name: big-pipeline
            jobs:
              build:
                steps:
                  - name: big
                    run: head -c 6000 /dev/zero | tr '\0' 'a'
            """);

        SetRepoDirectory();

        var runResult = await RunPipelineAsync(TestConsoleFactory.Create(), "big.yaml");
        Assert.Equal(0, runResult);

        var (query, service) = ResolveHistory();
        var repoPath = Directory.GetCurrentDirectory();

        var runs = await query.ListRunsAsync(repoPath, 1, TestContext.Current.CancellationToken);
        var run = await service.GetRunAsync(repoPath, runs[0].Id, TestContext.Current.CancellationToken);

        var output = run.Jobs.First().Steps.First().Output;
        Assert.Equal(4000, output.Length);
    }

    [Fact]
    public async Task GetRunAsync_UnknownRunId_ThrowsArgumentException()
    {
        _repo.WritePipeline("build.yaml", """
            name: build-pipeline
            jobs:
              build:
                steps:
                  - name: echo
                    run: echo "run"
            """);

        SetRepoDirectory();

        var runResult = await RunPipelineAsync(TestConsoleFactory.Create(), "build.yaml");
        Assert.Equal(0, runResult);

        var (_, service) = ResolveHistory();
        var repoPath = Directory.GetCurrentDirectory();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetRunAsync(repoPath, 99999, TestContext.Current.CancellationToken));

        Assert.Contains("99999", exception.Message);
    }

    [Fact]
    public async Task ListRunsAsync_RespectsLimit()
    {
        _repo.WritePipeline("build.yaml", """
            name: build-pipeline
            jobs:
              build:
                steps:
                  - name: echo
                    run: echo "run"
            """);

        SetRepoDirectory();

        for (var i = 0; i < 3; i++)
        {
            var result = await RunPipelineAsync(TestConsoleFactory.Create(), "build.yaml");
            Assert.Equal(0, result);
        }

        var (query, _) = ResolveHistory();
        var repoPath = Directory.GetCurrentDirectory();

        var runs = await query.ListRunsAsync(repoPath, 2, TestContext.Current.CancellationToken);
        Assert.Equal(2, runs.Count);
    }
}