using Cli.Commands;
using Cli.Handlers;
using Cli.Helpers;
using Cli.Infrastructure;
using Cli.Services;
using Microsoft.EntityFrameworkCore;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Integration;

[Collection("Integration Tests")]
public class ValidateCommandTests : IDisposable
{
    private readonly TestRailwayRepository _repo;
    private readonly string _originalDir;

    public ValidateCommandTests()
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

    [Fact]
    public async Task Validate_ValidPipeline_ShowsValidOutput()
    {
        var pipelinePath = _repo.GetPipelinePath("build.yaml");
        _repo.WritePipeline("build.yaml", """
            name: build-pipeline
            jobs:
              build:
                steps:
                  - name: compile
                    run: dotnet build
            """);

        SetRepoDirectory();
        var console = TestConsoleFactory.Create();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<ValidateCommand>("validate");
        });

        var result = await app.RunAsync(["validate", pipelinePath], TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        Assert.Contains("Pipeline: build-pipeline", console.Output);
        Assert.Contains("Pipeline is valid", console.Output);
        Assert.Contains("Jobs: 1", console.Output);
        Assert.Contains("build", console.Output);
        Assert.Contains("compile", console.Output);
    }

    [Fact]
    public async Task Validate_InvalidPipeline_ShowsErrors()
    {
        var pipelinePath = _repo.GetPipelinePath("invalid.yaml");
        _repo.WritePipeline("invalid.yaml", """
            name: invalid-pipeline
            jobs:
              build:
                depends_on: [deploy]
                steps:
                  - name: compile
                    run: dotnet build
            """);

        SetRepoDirectory();
        var console = TestConsoleFactory.Create();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<ValidateCommand>("validate");
        });

        var result = await app.RunAsync(["validate", pipelinePath], TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        Assert.Contains("Pipeline is invalid", console.Output);
        Assert.Contains("Job 'deploy' does not exist", console.Output);
    }

    [Fact]
    public async Task Validate_NonYamlFile_ReturnsError()
    {
        var txtPath = Path.Combine(_repo.Path, "readme.txt");
        File.WriteAllText(txtPath, "not a yaml file");

        SetRepoDirectory();
        var console = TestConsoleFactory.Create();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<ValidateCommand>("validate");
        });

        var result = await app.RunAsync(["validate", txtPath], TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        Assert.Contains("Not a yaml file", console.Output);
    }

    [Fact]
    public async Task Validate_NonExistentFile_ReturnsError()
    {
        SetRepoDirectory();
        var console = TestConsoleFactory.Create();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<ValidateCommand>("validate");
        });

        var result = await app.RunAsync(["validate", "nonexistent.yaml"], TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        Assert.Contains("Not a valid path", console.Output);
    }
}
