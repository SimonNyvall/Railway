using ModelContextProtocol;
using Railway.Engine.Domain;
using Railway.Mcp.Tools;
using Xunit;

namespace Tests.Unit;

/// <summary>
/// Unit tests for <see cref="EngineExceptionMapper"/>: every Engine domain exception and
/// <see cref="ArgumentException"/> maps to a friendly <see cref="McpException"/>, an
/// unexpected exception maps to a generic message, and an existing <see cref="McpException"/>
/// passes through unchanged.
/// </summary>
public class EngineExceptionMapperTests
{
    [Fact]
    public void Map_NotARailwayRepository_MentionsPathAndInitHint()
    {
        var mapped = EngineExceptionMapper.Map(new NotARailwayRepositoryException(), "/some/repo");

        Assert.Contains("No '.railway' directory was found under '/some/repo'", mapped.Message);
        Assert.Contains("railway init", mapped.Message);
    }

    [Fact]
    public void Map_NotARailwayRepository_NullPath_UsesPlaceholder()
    {
        var mapped = EngineExceptionMapper.Map(new NotARailwayRepositoryException());

        Assert.Contains("the provided path", mapped.Message);
    }

    [Fact]
    public void Map_NotAGitRepository_MentionsPath()
    {
        var mapped = EngineExceptionMapper.Map(new NotAGitRepositoryException(), "/some/repo");

        Assert.Contains("'/some/repo' is not inside a git repository", mapped.Message);
    }

    [Fact]
    public void Map_PipelineFileNotFound_MentionsFileName()
    {
        var mapped = EngineExceptionMapper.Map(new PipelineFileNotFoundException("build.yaml"), "/some/repo");

        Assert.Contains("Pipeline file 'build.yaml' was not found", mapped.Message);
        Assert.Contains("list_pipelines", mapped.Message);
    }

    [Fact]
    public void Map_AmbiguousPipeline_ListsCandidates()
    {
        var exception = new AmbiguousPipelineException("build.yaml", ["/a/build.yaml", "/b/build.yaml"]);

        var mapped = EngineExceptionMapper.Map(exception, "/some/repo");

        Assert.Contains("Multiple pipeline files match 'build.yaml'", mapped.Message);
        Assert.Contains("/a/build.yaml", mapped.Message);
        Assert.Contains("/b/build.yaml", mapped.Message);
    }

    [Fact]
    public void Map_PipelineInvalid_JoinsErrors()
    {
        var exception = new PipelineInvalidException(
            "build.yaml",
            ["Job 'deploy' does not exist", "Step must contain a 'run' command"],
            PipelineInvalidKind.Validation);

        var mapped = EngineExceptionMapper.Map(exception, "/some/repo");

        Assert.Contains("Pipeline 'build.yaml' is invalid", mapped.Message);
        Assert.Contains("Job 'deploy' does not exist; Step must contain a 'run' command", mapped.Message);
    }

    [Fact]
    public void Map_ArgumentException_PreservesMessage()
    {
        var mapped = EngineExceptionMapper.Map(new ArgumentException("Run 5 was not found."));

        Assert.Equal("Run 5 was not found.", mapped.Message);
    }

    [Fact]
    public void Map_UnexpectedException_ReturnsGenericMessage()
    {
        var mapped = EngineExceptionMapper.Map(new InvalidOperationException("boom"));

        Assert.Equal("An unexpected error occurred while executing the tool.", mapped.Message);
        Assert.DoesNotContain("boom", mapped.Message);
    }

    [Fact]
    public void Map_ExistingMcpException_PassesThrough()
    {
        var original = new McpException("already mapped");

        var mapped = EngineExceptionMapper.Map(original);

        Assert.Same(original, mapped);
    }

    [Fact]
    public async Task GuardAsync_ReturnsActionResult()
    {
        var result = await EngineExceptionMapper.GuardAsync(() => Task.FromResult(42));

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task GuardAsync_MapsThrownException()
    {
        var mapped = await Assert.ThrowsAsync<McpException>(() =>
            EngineExceptionMapper.GuardAsync<int>(() => throw new NotARailwayRepositoryException(), "/some/repo"));

        Assert.Contains("No '.railway' directory was found under '/some/repo'", mapped.Message);
    }
}
