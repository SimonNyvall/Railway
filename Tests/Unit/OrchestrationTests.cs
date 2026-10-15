using Railway.Engine.Domain;
using Railway.Engine.Orchestration;
using Railway.Engine.Services.Interfaces;
using FakeItEasy;
using Xunit;

namespace Tests.Unit;

/// <summary>
/// Unit tests for the Engine orchestration layer (step 8). The orchestrators take
/// an explicit start path, so these tests compose real orchestrators over temp
/// directories without touching the process cwd or the console.
/// </summary>
public class OrchestrationTests : IDisposable
{
    private readonly string _root;

    public OrchestrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "railway_orchestration_" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateGitRepo()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        return repo;
    }

    private static IPipelineDiscoveryService DiscoveryReturning(params DiscoveredPipeline[] pipelines)
    {
        var discovery = A.Fake<IPipelineDiscoveryService>();
        IReadOnlyList<DiscoveredPipeline> discovered = pipelines;
        A.CallTo(() => discovery.Discover(A<string>._)).Returns(discovered);
        return discovery;
    }

    [Fact]
    public async Task RepositoryInitializer_NoGitRoot_ThrowsNotAGitRepository()
    {
        var start = Path.Combine(_root, "nogit");
        Directory.CreateDirectory(start);

        var initializer = new RepositoryInitializer();

        var exception = await Assert.ThrowsAsync<NotAGitRepositoryException>(
            () => initializer.InitializeAsync(start, TestContext.Current.CancellationToken));

        Assert.Equal(NotAGitRepositoryException.FatalMessage, exception.Message);
    }

    [Fact]
    public async Task RepositoryInitializer_CreatesStructure_ThenReportsNotCreated()
    {
        var repo = CreateGitRepo();
        var initializer = new RepositoryInitializer();

        var first = await initializer.InitializeAsync(repo, TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded);
        Assert.True(first.Created);
        Assert.Equal(repo, first.RootRepositoryPath);
        Assert.True(Directory.Exists(Path.Combine(repo, ".railway")));
        Assert.True(Directory.Exists(Path.Combine(repo, ".railway", "pipelines")));
        Assert.True(File.Exists(Path.Combine(repo, ".railway", "railway.yaml")));

        var second = await initializer.InitializeAsync(repo, TestContext.Current.CancellationToken);

        Assert.False(second.Created);
    }

    [Fact]
    public async Task PipelineValidator_NonYamlPath_ThrowsArgumentException()
    {
        var validator = new PipelineValidator(A.Fake<IValidateService>());

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => validator.ValidateAsync("pipeline.txt", TestContext.Current.CancellationToken));

        Assert.Contains("Not a yaml file", exception.Message);
    }

    [Fact]
    public async Task PipelineLister_NoRailwayRepository_ThrowsNotARailwayRepository()
    {
        var lister = new PipelineLister(A.Fake<IPipelineDiscoveryService>());

        await Assert.ThrowsAsync<NotARailwayRepositoryException>(
            () => lister.ListAsync(_root, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PipelineRunner_NoMatch_ThrowsPipelineFileNotFound()
    {
        var repo = CreateGitRepo();
        Directory.CreateDirectory(Path.Combine(repo, ".railway"));

        var runner = new PipelineRunner(
            A.Fake<IRunnerService>(),
            DiscoveryReturning(),
            A.Fake<IValidateService>());

        var exception = await Assert.ThrowsAsync<PipelineFileNotFoundException>(
            () => runner.RunAsync(repo, "missing.yaml", verbose: false, ct: TestContext.Current.CancellationToken));

        Assert.Equal("missing.yaml", exception.FileName);
        Assert.Contains("Pipeline file not found", exception.Message);
    }

    [Fact]
    public async Task PipelineRunner_MultipleMatches_ThrowsAmbiguousWithCandidates()
    {
        var repo = CreateGitRepo();
        Directory.CreateDirectory(Path.Combine(repo, ".railway"));

        var runner = new PipelineRunner(
            A.Fake<IRunnerService>(),
            DiscoveryReturning(
                new DiscoveredPipeline { FileName = "build.yaml", Path = "/repo/ci/build.yaml" },
                new DiscoveredPipeline { FileName = "build.yaml", Path = "/repo/other/build.yaml" }),
            A.Fake<IValidateService>());

        var exception = await Assert.ThrowsAsync<AmbiguousPipelineException>(
            () => runner.RunAsync(repo, "build.yaml", verbose: false, ct: TestContext.Current.CancellationToken));

        Assert.Equal("build.yaml", exception.FileName);
        Assert.Equal(["/repo/ci/build.yaml", "/repo/other/build.yaml"], exception.Candidates);
    }
}
