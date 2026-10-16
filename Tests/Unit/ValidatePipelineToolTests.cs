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
/// Unit tests for <see cref="ValidatePipelineTool"/> driven by a FakeItEasy fake of
/// <see cref="IPipelineValidator"/>: asserts file resolution, DTO mapping, and error mapping.
/// </summary>
public class ValidatePipelineToolTests
{
    private static TempDirectory CreateRepository()
    {
        var repository = new TempDirectory();
        var pipelines = Path.Combine(repository.Path, ".railway", "pipelines");
        Directory.CreateDirectory(pipelines);
        File.WriteAllText(Path.Combine(pipelines, "build.yaml"), "name: build\njobs: {}\n");
        return repository;
    }

    private static IPipelineValidator ValidatorReturning(ValidationResult result)
    {
        var validator = A.Fake<IPipelineValidator>();
        A.CallTo(() => validator.ValidateAsync(A<string>._, A<CancellationToken>._)).Returns(result);
        return validator;
    }

    [Fact]
    public async Task ValidateAsync_RelativeFile_ResolvesUnderPipelinesFolderAndMapsResult()
    {
        using var repository = CreateRepository();
        var expectedFile = Path.Combine(repository.Path, ".railway", "pipelines", "build.yaml");
        var validator = ValidatorReturning(new ValidationResult(new Pipeline { Name = "build" }, []));
        var tool = new ValidatePipelineTool(validator);

        var result = await tool.ValidateAsync("build.yaml", repository.Path, TestContext.Current.CancellationToken);

        Assert.Equal("build", result.Name);
        Assert.Empty(result.Errors);
        A.CallTo(() => validator.ValidateAsync(expectedFile, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ValidateAsync_AbsoluteFile_IsPassedThroughUnchanged()
    {
        using var repository = CreateRepository();
        var file = Path.Combine(repository.Path, ".railway", "pipelines", "build.yaml");
        var validator = ValidatorReturning(new ValidationResult(new Pipeline { Name = "build" }, ["bad"]));
        var tool = new ValidatePipelineTool(validator);

        var result = await tool.ValidateAsync(file, repository.Path, TestContext.Current.CancellationToken);

        Assert.Equal("build", result.Name);
        Assert.Equal(["bad"], result.Errors);
        A.CallTo(() => validator.ValidateAsync(file, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ValidateAsync_MissingFileInRepository_ThrowsMcpException()
    {
        using var repository = CreateRepository();
        var validator = A.Fake<IPipelineValidator>();
        var tool = new ValidatePipelineTool(validator);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tool.ValidateAsync("missing.yaml", repository.Path, TestContext.Current.CancellationToken));

        Assert.Contains("Pipeline file 'missing.yaml' was not found", exception.Message);
        A.CallTo(() => validator.ValidateAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ValidateAsync_NonRailwayDirectory_ThrowsMcpException()
    {
        using var repository = new TempDirectory();
        var tool = new ValidatePipelineTool(A.Fake<IPipelineValidator>());

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tool.ValidateAsync("build.yaml", repository.Path, TestContext.Current.CancellationToken));

        Assert.Contains($"No '.railway' directory was found under '{repository.Path}'", exception.Message);
    }

    [Fact]
    public async Task ValidateAsync_DomainException_ThrowsMcpExceptionWithFriendlyMessage()
    {
        using var repository = CreateRepository();
        var validator = A.Fake<IPipelineValidator>();
        A.CallTo(() => validator.ValidateAsync(A<string>._, A<CancellationToken>._))
            .ThrowsAsync(new PipelineInvalidException(
                "build.yaml",
                ["Job 'deploy' does not exist"],
                PipelineInvalidKind.Validation));
        var tool = new ValidatePipelineTool(validator);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tool.ValidateAsync("build.yaml", repository.Path, TestContext.Current.CancellationToken));

        Assert.Contains("Pipeline 'build.yaml' is invalid", exception.Message);
        Assert.Contains("Job 'deploy' does not exist", exception.Message);
    }
}
