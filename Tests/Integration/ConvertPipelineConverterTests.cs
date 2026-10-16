using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Orchestration;
using Railway.Engine.Services;
using Railway.Engine.Services.Interfaces;

using FakeItEasy;

using Tests.Infrastructure;

using Xunit;

namespace Tests.Integration;

/// <summary>
/// Integration tests for <see cref="PipelineConverter"/>: they compose the real conversion service, the real YAML
/// writer, and the real validator against a temp git repository with a temp <c>.railway</c> folder.
/// </summary>
[Collection("Integration Tests")]
public class ConvertPipelineConverterTests : IDisposable
{
    private const string GitHubWorkflow = """
        name: CI
        on: [push]
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - name: Build
                run: dotnet build
          test:
            needs: build
            runs-on: ubuntu-latest
            steps:
              - name: Test
                run: dotnet test
        """;

    private readonly TestRailwayRepository _repo;
    private readonly string _originalDir;
    private readonly PipelineConverter _converter;

    public ConvertPipelineConverterTests()
    {
        _originalDir = Directory.GetCurrentDirectory();
        _repo = new TestRailwayRepository();
        _converter = new PipelineConverter(new PipelineConvertService(), new PipelineValidator(new ValidateService()));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        _repo.Dispose();
    }

    private string PipelinesFile(string fileName) =>
        Path.Combine(_repo.Path, ".railway", "pipelines", fileName);

    private string WriteSource(string relativePath, string content)
    {
        var fullPath = Path.Combine(_repo.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return relativePath;
    }

    [Fact]
    public async Task Convert_GitHubWorkflow_WritesPipelineThatReparsesAndValidates()
    {
        var source = WriteSource(Path.Combine(".github", "workflows", "ci.yml"), GitHubWorkflow);

        var result = await _converter.ConvertAsync(
            source, null, _repo.Path, null, false, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(ConversionStatus.Succeeded, result.Status);
        Assert.Equal(SourceFormat.GitHub, result.Format);
        Assert.Equal(PipelinesFile("ci.yaml"), result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Empty(result.ValidationErrors);

        var parsed = await PipelineHelper.Parse(result.OutputPath!);
        Assert.Equal("CI", parsed.Name);
        Assert.Equal(new[] { "build", "test" }, parsed.Jobs.Keys.OrderBy(key => key).ToArray());
        Assert.Contains("build", parsed.Jobs["test"].DependsOn);

        var validation = await new PipelineValidator(new ValidateService())
            .ValidateAsync(result.OutputPath!, TestContext.Current.CancellationToken);
        Assert.Empty(validation.Errors);
    }

    [Fact]
    public async Task Convert_OutputNameOverride_UsesNameWithAppendedYamlExtension()
    {
        WriteSource("workflow.yml", GitHubWorkflow);

        var result = await _converter.ConvertAsync(
            "workflow.yml", SourceFormat.GitHub, _repo.Path, "custom-name", false, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(PipelinesFile("custom-name.yaml"), result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    public async Task Convert_TargetExistsWithoutForce_ThrowsAndLeavesFileUnchanged()
    {
        var source = WriteSource(Path.Combine(".github", "workflows", "ci.yml"), GitHubWorkflow);
        var first = await _converter.ConvertAsync(
            source, null, _repo.Path, null, false, TestContext.Current.CancellationToken);
        var original = await File.ReadAllTextAsync(first.OutputPath!, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _converter.ConvertAsync(
            source, null, _repo.Path, null, false, TestContext.Current.CancellationToken));

        Assert.Contains("--force", exception.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(first.OutputPath!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Convert_TargetExistsWithForce_Overwrites()
    {
        var source = WriteSource(Path.Combine(".github", "workflows", "ci.yml"), GitHubWorkflow);
        var first = await _converter.ConvertAsync(
            source, null, _repo.Path, null, false, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(first.OutputPath!, "name: stale\njobs: {}\n", TestContext.Current.CancellationToken);

        var second = await _converter.ConvertAsync(
            source, null, _repo.Path, null, true, TestContext.Current.CancellationToken);

        Assert.True(second.Succeeded);
        var content = await File.ReadAllTextAsync(second.OutputPath!, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("stale", content);
        Assert.Contains("name: CI", content);
    }

    [Fact]
    public async Task Convert_ValidationFailingPipeline_ReportsErrorsAndKeepsFile()
    {
        // A stubbed conversion service returns a pipeline that the real validator rejects (dangling dependency),
        // which the real convert service can never produce.
        var convertService = A.Fake<IPipelineConvertService>();
        A.CallTo(() => convertService.DetectFormat(A<string>._)).Returns(SourceFormat.GitHub);
        A.CallTo(() => convertService.Convert(A<string>._, A<SourceFormat>._)).Returns(new ConversionOutcome(
            new Pipeline
            {
                Name = "Invalid",
                Jobs = new Dictionary<string, Job>
                {
                    ["build"] = new Job
                    {
                        DependsOn = ["missing"],
                        Steps = [new Step { Run = "dotnet build" }],
                    },
                },
            },
            ["job 'build': 'runs-on' cannot be mapped"],
            null));

        var converter = new PipelineConverter(convertService, new PipelineValidator(new ValidateService()));
        var source = WriteSource(Path.Combine(".github", "workflows", "ci.yml"), GitHubWorkflow);

        var result = await converter.ConvertAsync(
            source, null, _repo.Path, null, false, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(ConversionStatus.Succeeded, result.Status);
        Assert.NotEmpty(result.ValidationErrors);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Convert_UndetectableFormat_ReturnsFormatUndeterminedWithoutWriting()
    {
        WriteSource("mystery.yaml", "foo: bar\n");

        var result = await _converter.ConvertAsync(
            "mystery.yaml", null, _repo.Path, null, false, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ConversionStatus.FormatUndetermined, result.Status);
        Assert.Null(result.Format);
        Assert.Null(result.OutputPath);
        Assert.Null(result.Error);
        Assert.Empty(result.ValidationErrors);
        Assert.False(File.Exists(PipelinesFile("mystery.yaml")));
    }

    [Fact]
    public async Task Convert_UnconvertibleSource_ReturnsConversionFailedWithoutWriting()
    {
        WriteSource("workflow.yml", "not: [a, ci, pipeline]\n");

        var result = await _converter.ConvertAsync(
            "workflow.yml", SourceFormat.GitHub, _repo.Path, null, false, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ConversionStatus.ConversionFailed, result.Status);
        Assert.Equal(SourceFormat.GitHub, result.Format);
        Assert.NotNull(result.Error);
        Assert.Null(result.OutputPath);
        Assert.Empty(Directory.GetFiles(Path.Combine(_repo.Path, ".railway", "pipelines")));
    }

    [Fact]
    public async Task Convert_NoRailwayFolder_ThrowsNotARailwayRepository()
    {
        using var directory = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, ".git"));
        var source = Path.Combine(directory.Path, "workflow.yml");
        await File.WriteAllTextAsync(source, GitHubWorkflow, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<NotARailwayRepositoryException>(() => _converter.ConvertAsync(
            source, SourceFormat.GitHub, directory.Path, null, false, TestContext.Current.CancellationToken));
    }
}
