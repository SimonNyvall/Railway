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

public class DashboardCommandTests : IDisposable
{
    private readonly TestRailwayRepository _repo;
    private readonly string _originalDir;
    private readonly string _databasePath;

    public DashboardCommandTests()
    {
        _originalDir = Directory.GetCurrentDirectory();
        _repo = new TestRailwayRepository();
        _databasePath = Path.Combine(Path.GetTempPath(), "railway_test_db_" + Guid.NewGuid());
        TestServiceRegistrar.RegisterServices(_databasePath);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        _repo.Dispose();
        if (Directory.Exists(_databasePath))
        {
            Directory.Delete(_databasePath, true);
        }
    }

    private void SetRepoDirectory()
    {
        Directory.SetCurrentDirectory(_repo.Path);
    }

    [Fact]
    public async Task Dashboard_ShowsRepositoryName()
    {
        SetRepoDirectory();
        var console = new TestConsole();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<DashboardCommand>("");
        });

        var result = await app.RunAsync([]);

        Assert.Equal(0, result);
        Assert.Contains("Repository:", console.Output);
        Assert.Contains(new DirectoryInfo(_repo.Path).Name, console.Output);
    }

    [Fact]
    public async Task Dashboard_NoPipelines_ShowsEmptyMessage()
    {
        SetRepoDirectory();
        var console = new TestConsole();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<DashboardCommand>("");
        });

        var result = await app.RunAsync([]);

        Assert.Equal(0, result);
        Assert.Contains("Pipelines:", console.Output);
        Assert.Contains("No pipelines configured", console.Output);
    }

    [Fact]
    public async Task Dashboard_WithPipelines_ShowsPipelineList()
    {
        _repo.WritePipeline("build.yaml", """
            name: build-pipeline
            jobs:
              build:
                steps:
                  - name: compile
                    run: dotnet build
            """);

        _repo.WritePipeline("test.yaml", """
            name: test-pipeline
            jobs:
              test:
                steps:
                  - name: test
                    run: dotnet test
            """);

        SetRepoDirectory();
        var console = new TestConsole();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<DashboardCommand>("");
        });

        var result = await app.RunAsync([]);

        Assert.Equal(0, result);
        Assert.Contains("build.yaml - name: build-pipeline", console.Output);
        Assert.Contains("test.yaml - name: test-pipeline", console.Output);
    }

    [Fact]
    public async Task Dashboard_WithInvalidPipeline_ShowsAsInvalid()
    {
        _repo.WritePipeline("invalid.yaml", "invalid: yaml: [");

        SetRepoDirectory();
        var console = new TestConsole();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<DashboardCommand>("");
        });

        var result = await app.RunAsync([]);

        Assert.Equal(0, result);
        Assert.Contains("invalid.yaml", console.Output);
        Assert.Contains("(invalid)", console.Output);
    }

    [Fact]
    public async Task Dashboard_ShowsRecentRuns()
    {
        _repo.WritePipeline("build.yaml", """
            name: build-pipeline
            jobs:
              build:
                steps:
                  - name: echo
                    run: echo "Test"
            """);

        SetRepoDirectory();
        var runConsole = new TestConsole();
        var runRegistrar = new TestTypeRegistrar(runConsole);
        var runApp = new CommandApp(runRegistrar);
        runApp.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<RunCommand>("run");
        });

        await runApp.RunAsync(["run", "build.yaml"]);

        var console = new TestConsole();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<RunCommand>("run");
            c.AddCommand<DashboardCommand>("");
        });

        var result = await app.RunAsync([]);

        Assert.Equal(0, result);
        Assert.Contains("Recent Runs:", console.Output);
        Assert.Contains("build.yaml", console.Output);
        Assert.Contains("build-pipeline", console.Output);
        Assert.Contains("Succeeded", console.Output);
    }

    [Fact]
    public async Task Dashboard_ShowsFailedRun()
    {
        _repo.WritePipeline("fail.yaml", """
            name: fail-pipeline
            jobs:
              build:
                steps:
                  - name: fail
                    run: exit 1
            """);

        SetRepoDirectory();
        var runConsole = new TestConsole();
        var runRegistrar = new TestTypeRegistrar(runConsole);
        var runApp = new CommandApp(runRegistrar);
        runApp.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<RunCommand>("run");
        });

        await runApp.RunAsync(["run", "fail.yaml"]);

        var console = new TestConsole();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<RunCommand>("run");
            c.AddCommand<DashboardCommand>("");
        });

        var result = await app.RunAsync([]);

        Assert.Equal(0, result);
        Assert.Contains("Recent Runs:", console.Output);
        Assert.Contains("fail.yaml", console.Output);
        Assert.Contains("fail-pipeline", console.Output);
        Assert.Contains("Failed", console.Output);
    }
}
