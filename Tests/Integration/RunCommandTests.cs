using Cli.Commands;
using Cli.Domain;
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

public class RunCommandTests : IDisposable
{
    private readonly TestRailwayRepository _repo;
    private readonly string _originalDir;

    public RunCommandTests()
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
    public async Task Run_SimplePipeline_Succeeds()
    {
        _repo.WritePipeline("build.yaml", """
            name: build-pipeline
            jobs:
              build:
                steps:
                  - name: echo
                    run: echo "Hello World"
            """);

        SetRepoDirectory();
        var console = new TestConsole();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<RunCommand>("run");
        });

        var result = await app.RunAsync(["run", "build.yaml"]);

        Assert.Equal(0, result);
        Assert.Contains("Pipeline completed successfully", console.Output);
        Assert.Contains("Hello World", console.Output);
    }

    [Fact]
    public async Task Run_PipelineWithDependencies_ExecutesInOrder()
    {
        _repo.WritePipeline("build.yaml", """
            name: build-pipeline
            jobs:
              first:
                steps:
                  - name: step1
                    run: echo "First"
              second:
                depends_on: [first]
                steps:
                  - name: step2
                    run: echo "Second"
              third:
                depends_on: [second]
                steps:
                  - name: step3
                    run: echo "Third"
            """);

        SetRepoDirectory();
        var console = new TestConsole();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<RunCommand>("run");
        });

        var result = await app.RunAsync(["run", "build.yaml"]);

        Assert.Equal(0, result);
        Assert.Contains("Pipeline completed successfully", console.Output);

        // Verify order - First should appear before Second, Second before Third
        var firstIndex = console.Output.IndexOf("First");
        var secondIndex = console.Output.IndexOf("Second");
        var thirdIndex = console.Output.IndexOf("Third");

        Assert.True(firstIndex < secondIndex, "First should execute before Second");
        Assert.True(secondIndex < thirdIndex, "Second should execute before Third");
    }

    [Fact]
    public async Task Run_FailingStep_StopsPipeline()
    {
        _repo.WritePipeline("build.yaml", """
            name: build-pipeline
            jobs:
              build:
                steps:
                  - name: succeed
                    run: echo "Success"
                  - name: fail
                    run: exit 1
                  - name: should-not-run
                    run: echo "Should not run"
            """);

        SetRepoDirectory();
        var console = new TestConsole();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<RunCommand>("run");
        });

        var result = await app.RunAsync(["run", "build.yaml"]);

        Assert.Equal(1, result);
        Assert.Contains("Pipeline failed", console.Output);
        Assert.Contains("Success", console.Output);
        Assert.DoesNotContain("Should not run", console.Output);
    }

    [Fact]
    public async Task Run_NonExistentPipeline_ShowsError()
    {
        SetRepoDirectory();
        var console = new TestConsole();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<RunCommand>("run");
        });

        var result = await app.RunAsync(["run", "nonexistent.yaml"]);

        Assert.Equal(1, result);
        Assert.Contains("Pipeline file not found", console.Output);
    }

    [Fact]
    public async Task Run_InvalidPipeline_ShowsValidationError()
    {
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
        var console = new TestConsole();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<RunCommand>("run");
        });

        var result = await app.RunAsync(["run", "invalid.yaml"]);

        Assert.Equal(1, result);
        Assert.Contains("Pipeline is invalid", console.Output);
    }
}
