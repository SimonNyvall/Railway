using Cli.Domain;
using Cli.Handlers;
using Cli.Services.Interfaces;
using FakeItEasy;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Unit;

/// <summary>
/// Unit tests for the real <see cref="ValidationHandler"/> using a FakeItEasy fake
/// for <see cref="IValidateService"/>. The handler parses the supplied yaml file
/// with the real <see cref="Cli.Helpers.PipelineHelper"/>, so valid/invalid cases
/// write a real temp file.
/// <para>
/// Serialized with the <c>Integration Tests</c> collection because the handler
/// writes to the process-wide <see cref="Spectre.Console.AnsiConsole"/>.
/// </para>
/// </summary>
[Collection("Integration Tests")]
public class ValidationHandlerTests
{
    private static IValidateService Validating(IEnumerable<string> errors)
    {
        var validate = A.Fake<IValidateService>();
        A.CallTo(() => validate.IsValid(A<Pipeline>._)).Returns(errors);
        return validate;
    }

    [Fact]
    public async Task Handle_NonYamlPath_ThrowsArgumentException()
    {
        var handler = new ValidationHandler(A.Fake<IValidateService>());

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => handler.Handle("pipeline.txt"));

        Assert.Contains("Not a yaml file", exception.Message);
    }

    [Fact]
    public async Task Handle_MissingYamlFile_ThrowsArgumentException()
    {
        var handler = new ValidationHandler(A.Fake<IValidateService>());

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => handler.Handle("definitely-missing.yaml"));

        Assert.Contains("Not a valid path", exception.Message);
    }

    [Fact]
    public async Task Handle_ValidPipeline_ReturnsTrueAndRendersDetails()
    {
        const string yaml = """
            name: build-pipeline
            jobs:
              build:
                steps:
                  - name: compile
                    run: dotnet build
              test:
                depends_on: [build]
                steps:
                  - name: unit-tests
                    run: dotnet test
            """;

        using var directory = new TempDirectory();
        var path = directory.File("pipeline.yaml");
        File.WriteAllText(path, yaml);

        var validate = Validating([]);
        var console = TestConsoleFactory.Create();
        var handler = new ValidationHandler(validate);

        var result = await handler.Handle(path);

        Assert.True(result);
        Assert.Contains("Pipeline: build-pipeline", console.Output);
        Assert.Contains("Pipeline is valid", console.Output);
        Assert.Contains("Jobs: 2", console.Output);
        Assert.Contains("build", console.Output);
        Assert.Contains("Depends on: build", console.Output);
        Assert.Contains("dotnet test", console.Output);
        A.CallTo(() => validate.IsValid(A<Pipeline>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Handle_InvalidPipeline_ReturnsFalseAndRendersErrors()
    {
        const string yaml = """
            name: build-pipeline
            jobs:
              build:
                steps:
                  - name: compile
                    run: dotnet build
            """;

        using var directory = new TempDirectory();
        var path = directory.File("pipeline.yaml");
        File.WriteAllText(path, yaml);

        var validate = Validating(["Job 'deploy' does not exist", "Step must contain a 'run' command"]);
        var console = TestConsoleFactory.Create();
        var handler = new ValidationHandler(validate);

        var result = await handler.Handle(path);

        Assert.False(result);
        Assert.Contains("Pipeline is invalid", console.Output);
        Assert.Contains("Errors:", console.Output);
        Assert.Contains("Job 'deploy' does not exist", console.Output);
        Assert.Contains("Step must contain a 'run' command", console.Output);
    }
}
