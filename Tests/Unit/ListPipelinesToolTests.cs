using ModelContextProtocol;
using Railway.Engine.Domain;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Mcp.Tools;
using FakeItEasy;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Unit;

/// <summary>
/// Unit tests for <see cref="ListPipelinesTool"/> driven by a FakeItEasy fake of
/// <see cref="IPipelineLister"/>: asserts DTO mapping, that the resolved path is forwarded
/// to the Engine, and that a domain exception becomes a friendly <see cref="McpException"/>.
/// </summary>
public class ListPipelinesToolTests
{
    [Fact]
    public async Task ListAsync_MapsDiscoveredPipelinesToSummaries()
    {
        using var repository = new TempDirectory();
        IReadOnlyList<DiscoveredPipeline> discovered =
        [
            new DiscoveredPipeline
            {
                FileName = "build.yaml",
                Path = Path.Combine(repository.Path, "build.yaml"),
                Name = "build",
                IsValid = true,
            },
            new DiscoveredPipeline
            {
                FileName = "deploy.yaml",
                Path = Path.Combine(repository.Path, "deploy.yaml"),
                Name = null,
                IsValid = false,
                Errors = ["Job 'x' does not exist"],
            },
        ];

        var lister = A.Fake<IPipelineLister>();
        A.CallTo(() => lister.ListAsync(repository.Path, A<CancellationToken>._)).Returns(discovered);

        var tool = new ListPipelinesTool(lister);

        var result = await tool.ListAsync(repository.Path, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);

        Assert.Equal("build.yaml", result[0].FileName);
        Assert.Equal(discovered[0].Path, result[0].Path);
        Assert.Equal("build", result[0].Name);
        Assert.True(result[0].IsValid);
        Assert.Empty(result[0].Errors);

        Assert.Equal("deploy.yaml", result[1].FileName);
        Assert.Null(result[1].Name);
        Assert.False(result[1].IsValid);
        Assert.Equal(["Job 'x' does not exist"], result[1].Errors);
    }

    [Fact]
    public async Task ListAsync_ExplicitPath_IsForwardedToEngine()
    {
        using var repository = new TempDirectory();
        var lister = A.Fake<IPipelineLister>();
        A.CallTo(() => lister.ListAsync(A<string>._, A<CancellationToken>._))
            .Returns(Array.Empty<DiscoveredPipeline>());

        var tool = new ListPipelinesTool(lister);

        await tool.ListAsync(repository.Path, TestContext.Current.CancellationToken);

        A.CallTo(() => lister.ListAsync(repository.Path, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ListAsync_DomainException_ThrowsMcpExceptionWithFriendlyMessage()
    {
        using var repository = new TempDirectory();
        var lister = A.Fake<IPipelineLister>();
        A.CallTo(() => lister.ListAsync(A<string>._, A<CancellationToken>._))
            .ThrowsAsync(new NotARailwayRepositoryException());

        var tool = new ListPipelinesTool(lister);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tool.ListAsync(repository.Path, TestContext.Current.CancellationToken));

        Assert.Contains($"No '.railway' directory was found under '{repository.Path}'", exception.Message);
    }

    [Fact]
    public async Task ListAsync_NonexistentPath_ThrowsMcpException()
    {
        var missing = Path.Combine(Path.GetTempPath(), "railway-missing-" + Guid.NewGuid().ToString("N"));
        var tool = new ListPipelinesTool(A.Fake<IPipelineLister>());

        await Assert.ThrowsAsync<McpException>(
            () => tool.ListAsync(missing, TestContext.Current.CancellationToken));
    }
}
