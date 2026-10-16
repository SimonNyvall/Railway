using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Orchestration;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Engine.Services.Interfaces;

using FakeItEasy;

using Tests.Infrastructure;

using Xunit;

namespace Tests.Unit;

/// <summary>
/// Unit tests for <see cref="PipelineConverter"/>. The conversion service and the validator are mocked; the only real
/// collaborators are the YAML writer and the filesystem, so each test runs against a temp directory that contains the
/// <c>.git</c> folder and <c>.railway/pipelines</c> folder the repository resolution walks up to find.
/// </summary>
public class PipelineConverterTests : IDisposable
{
    private readonly TempDirectory _tempDirectory = new();
    private readonly string _root;
    private readonly IPipelineConvertService _convertService;
    private readonly IPipelineValidator _validator;
    private readonly PipelineConverter _converter;

    public PipelineConverterTests()
    {
        _root = _tempDirectory.Path;
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, ".railway", "pipelines"));

        _convertService = A.Fake<IPipelineConvertService>();
        _validator = A.Fake<IPipelineValidator>();
        _converter = new PipelineConverter(_convertService, _validator);
    }

    /// <inheritdoc/>
    public void Dispose() => _tempDirectory.Dispose();

    private string SourcePath(string fileName = "ci.yml") => Path.Combine(_root, fileName);

    private string PipelinePath(string fileName) => Path.Combine(_root, ".railway", "pipelines", fileName);

    private void StubValidConversion(Pipeline pipeline, SourceFormat format, params string[] warnings)
    {
        A.CallTo(() => _convertService.Convert(A<string>._, format))
            .Returns(new ConversionOutcome(pipeline, warnings, null));
        A.CallTo(() => _validator.ValidateAsync(A<string>._, A<CancellationToken>._))
            .Returns(new ValidationResult(pipeline, []));
    }

    private Task<ConversionResult> ConvertAsync(
        string sourcePath,
        SourceFormat? formatHint = null,
        string? outputName = null,
        bool force = false) =>
        _converter.ConvertAsync(
            sourcePath, formatHint, _root, outputName, force, TestContext.Current.CancellationToken);

    private static Pipeline SamplePipeline() => new()
    {
        Name = "CI",
        Jobs = new Dictionary<string, Job>
        {
            ["build"] = new Job { Steps = [new Step { Run = "dotnet build" }] },
        },
    };

    // ------------------------------------------------------------------
    // Format resolution
    // ------------------------------------------------------------------

    [Fact]
    public async Task Convert_WithFormatHint_UsesHintWithoutConsultingDetection()
    {
        var pipeline = SamplePipeline();
        StubValidConversion(pipeline, SourceFormat.GitLab);

        var result = await ConvertAsync(SourcePath(), SourceFormat.GitLab);

        Assert.Equal(ConversionStatus.Succeeded, result.Status);
        Assert.Equal(SourceFormat.GitLab, result.Format);
        A.CallTo(() => _convertService.Convert(SourcePath(), SourceFormat.GitLab)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _convertService.DetectFormat(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Convert_WithoutHint_DetectsFormatFromSource()
    {
        var pipeline = SamplePipeline();
        A.CallTo(() => _convertService.DetectFormat(SourcePath()))
            .Returns(SourceFormat.Azure);
        StubValidConversion(pipeline, SourceFormat.Azure);

        var result = await ConvertAsync(SourcePath());

        Assert.Equal(ConversionStatus.Succeeded, result.Status);
        Assert.Equal(SourceFormat.Azure, result.Format);
    }

    [Fact]
    public async Task Convert_UndeterminedFormat_ReturnsFormatUndeterminedWithoutWriting()
    {
        A.CallTo(() => _convertService.DetectFormat(A<string>._)).Returns((SourceFormat?)null);

        var result = await ConvertAsync(SourcePath());

        Assert.False(result.Succeeded);
        Assert.Equal(ConversionStatus.FormatUndetermined, result.Status);
        Assert.Null(result.Format);
        Assert.Null(result.OutputPath);
        Assert.Null(result.Error);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.ValidationErrors);
        Assert.False(File.Exists(PipelinePath("ci.yaml")));
        A.CallTo(() => _convertService.Convert(A<string>._, A<SourceFormat>._)).MustNotHaveHappened();
    }

    // ------------------------------------------------------------------
    // Conversion failures
    // ------------------------------------------------------------------

    [Fact]
    public async Task Convert_ConversionError_ReturnsConversionFailedWithErrorAndWarnings()
    {
        A.CallTo(() => _convertService.DetectFormat(A<string>._)).Returns(SourceFormat.GitHub);
        A.CallTo(() => _convertService.Convert(A<string>._, SourceFormat.GitHub))
            .Returns(new ConversionOutcome(null, ["'on' cannot be mapped"], "no convertible jobs"));

        var result = await ConvertAsync(SourcePath());

        Assert.False(result.Succeeded);
        Assert.Equal(ConversionStatus.ConversionFailed, result.Status);
        Assert.Equal(SourceFormat.GitHub, result.Format);
        Assert.Equal("no convertible jobs", result.Error);
        Assert.Equal(new[] { "'on' cannot be mapped" }, result.Warnings);
        Assert.Null(result.OutputPath);
        Assert.False(File.Exists(PipelinePath("ci.yaml")));
        A.CallTo(() => _validator.ValidateAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Convert_ErrorWithPipelineStillPresent_ReturnsConversionFailed()
    {
        A.CallTo(() => _convertService.DetectFormat(A<string>._)).Returns(SourceFormat.GitHub);
        A.CallTo(() => _convertService.Convert(A<string>._, SourceFormat.GitHub))
            .Returns(new ConversionOutcome(SamplePipeline(), [], "serialization failed"));

        var result = await ConvertAsync(SourcePath());

        Assert.Equal(ConversionStatus.ConversionFailed, result.Status);
        Assert.Equal("serialization failed", result.Error);
        Assert.False(File.Exists(PipelinePath("ci.yaml")));
    }

    // ------------------------------------------------------------------
    // Output-name derivation
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null, "ci.yaml")]
    [InlineData("custom-name", "custom-name.yaml")]
    [InlineData("custom-name.yaml", "custom-name.yaml")]
    [InlineData("custom-name.yml", "custom-name.yml")]
    public async Task Convert_DerivesOutputFileName(string? outputName, string expectedFileName)
    {
        StubValidConversion(SamplePipeline(), SourceFormat.GitHub);
        A.CallTo(() => _convertService.DetectFormat(A<string>._)).Returns(SourceFormat.GitHub);

        var result = await ConvertAsync(SourcePath("ci.yml"), outputName: outputName);

        Assert.Equal(PipelinePath(expectedFileName), result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    public async Task Convert_DefaultOutputName_ComesFromSourceFileName()
    {
        StubValidConversion(SamplePipeline(), SourceFormat.GitHub);
        A.CallTo(() => _convertService.DetectFormat(A<string>._)).Returns(SourceFormat.GitHub);

        var source = Path.Combine(_root, ".github", "workflows", "release.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);

        var result = await ConvertAsync(source);

        Assert.Equal(PipelinePath("release.yaml"), result.OutputPath);
    }

    // ------------------------------------------------------------------
    // Overwrite handling
    // ------------------------------------------------------------------

    [Fact]
    public async Task Convert_TargetExistsWithoutForce_Throws()
    {
        StubValidConversion(SamplePipeline(), SourceFormat.GitHub);
        A.CallTo(() => _convertService.DetectFormat(A<string>._)).Returns(SourceFormat.GitHub);
        await File.WriteAllTextAsync(PipelinePath("ci.yaml"), "name: existing\n", TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ConvertAsync(SourcePath()));

        Assert.Contains("already exists", exception.Message);
        Assert.Contains("--force", exception.Message);
        Assert.Equal("name: existing\n", await File.ReadAllTextAsync(PipelinePath("ci.yaml"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Convert_TargetExistsWithForce_Overwrites()
    {
        StubValidConversion(SamplePipeline(), SourceFormat.GitHub);
        A.CallTo(() => _convertService.DetectFormat(A<string>._)).Returns(SourceFormat.GitHub);
        await File.WriteAllTextAsync(PipelinePath("ci.yaml"), "name: existing\n", TestContext.Current.CancellationToken);

        var result = await ConvertAsync(SourcePath(), force: true);

        Assert.True(result.Succeeded);
        var content = await File.ReadAllTextAsync(PipelinePath("ci.yaml"), TestContext.Current.CancellationToken);
        Assert.Contains("name: CI", content);
        Assert.DoesNotContain("existing", content);
    }

    // ------------------------------------------------------------------
    // Warnings and validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task Convert_PropagatesWarningsFromOutcome()
    {
        var pipeline = SamplePipeline();
        StubValidConversion(
            pipeline,
            SourceFormat.GitHub,
            "job 'build': 'runs-on' cannot be mapped",
            "'on' cannot be mapped");

        var result = await ConvertAsync(SourcePath(), SourceFormat.GitHub);

        Assert.True(result.Succeeded);
        Assert.Equal(
            new[] { "job 'build': 'runs-on' cannot be mapped", "'on' cannot be mapped" },
            result.Warnings);
    }

    [Fact]
    public async Task Convert_ValidationErrors_ArePropagatedAndFileRemainsWritten()
    {
        var pipeline = SamplePipeline();
        A.CallTo(() => _convertService.Convert(A<string>._, SourceFormat.GitHub))
            .Returns(new ConversionOutcome(pipeline, [], null));
        A.CallTo(() => _validator.ValidateAsync(A<string>._, A<CancellationToken>._))
            .Returns(new ValidationResult(pipeline, ["Job 'build' has no steps"]));

        var result = await ConvertAsync(SourcePath(), SourceFormat.GitHub);

        Assert.True(result.Succeeded);
        Assert.Equal(ConversionStatus.Succeeded, result.Status);
        Assert.Equal(new[] { "Job 'build' has no steps" }, result.ValidationErrors);
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    public async Task Convert_PassesWrittenPathToValidator()
    {
        StubValidConversion(SamplePipeline(), SourceFormat.GitHub);

        await ConvertAsync(SourcePath(), SourceFormat.GitHub);

        A.CallTo(() => _validator.ValidateAsync(PipelinePath("ci.yaml"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }
}
