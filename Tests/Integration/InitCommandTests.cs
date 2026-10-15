using Cli.Commands;
using Cli.Handlers;
using Cli.Handlers.Interfaces;
using Railway.Engine;
using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Integration;

[Collection("Integration Tests")]
public class InitCommandTests : IDisposable
{
    private readonly IntegrationTestFixture _fixture;
    private readonly string _tempDir;
    private readonly string _originalDir;

    public InitCommandTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _originalDir = Directory.GetCurrentDirectory();
        _tempDir = Path.Combine(Path.GetTempPath(), "railway_init_test_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        Directory.SetCurrentDirectory(_tempDir);

        // Initialize git repo
        RunGit("init");
        RunGit("config user.email test@test.com");
        RunGit("config user.name Test User");
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private TestConsole CreateConsole() => new();

    private CommandApp CreateApp(TestConsole console)
    {
        var registrar = new TestTypeRegistrar(console, _fixture.DbPath);
        var app = new CommandApp(registrar);
        app.Configure(c => c.AddCommand<InitCommand>("init"));
        // Capture AnsiConsole output to TestConsole
        AnsiConsole.Console = console;
        return app;
    }

    [Fact]
    public async Task Init_CreatesRailwayStructure()
    {
        var console = CreateConsole();
        var app = CreateApp(console);

        // Resolve the handler through MS.DI
        var services = new ServiceCollection();
        services.AddEngine(new EngineOptions { DatabasePath = _fixture.DbPath });
        services.AddTransient<IInitHandler, InitHandler>();
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IInitHandler>();
        Assert.NotNull(handler);

        // Test help to see registered commands (output goes to stdout, not TestConsole)
        var helpResult = await app.RunAsync(["--help"], TestContext.Current.CancellationToken);
        Assert.Equal(0, helpResult);

        // Now test via app
        var result = await app.RunAsync(["init"], TestContext.Current.CancellationToken);

        Assert.Equal(0, result);

        var railwayPath = Path.Combine(_tempDir, ".railway");
        Assert.True(Directory.Exists(railwayPath));
        Assert.True(Directory.Exists(Path.Combine(railwayPath, "pipelines")));
        Assert.True(File.Exists(Path.Combine(railwayPath, "railway.yaml")));

        var manifestContent = await File.ReadAllTextAsync(Path.Combine(railwayPath, "railway.yaml"), TestContext.Current.CancellationToken);
        Assert.Contains("name:", manifestContent);
        Assert.Contains("pipelines:", manifestContent);
    }

    [Fact]
    public async Task Init_Idempotent_ReturnsReinitializedMessage()
    {
        var console = CreateConsole();
        var app = CreateApp(console);

        // First init
        await app.RunAsync(["init"], TestContext.Current.CancellationToken);

        // Second init
        var result = await app.RunAsync(["init"], TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        Assert.Contains("Reinitialized existing Railway repository", console.Output);
    }

    [Fact]
    public async Task Init_NotInGitRepo_ReturnsFatalError()
    {
        var nonGitDir = Path.Combine(Path.GetTempPath(), "railway_nogit_" + Guid.NewGuid());
        Directory.CreateDirectory(nonGitDir);
        var originalDir = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(nonGitDir);

            var console = CreateConsole();
            var app = CreateApp(console);

            var result = await app.RunAsync(["init"], TestContext.Current.CancellationToken);

            Assert.Equal(1, result);
            Assert.Contains("fatal: not a git repository", console.Output);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
            Directory.Delete(nonGitDir, true);
        }
    }

    private void RunGit(string args)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = _tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        process.WaitForExit();
    }
}