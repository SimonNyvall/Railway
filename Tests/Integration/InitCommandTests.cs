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

public class InitCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalDir;

    public InitCommandTests()
    {
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

    [Fact]
    public async Task Init_CreatesRailwayStructure()
    {
        var console = new TestConsole();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c => c.AddCommand<InitCommand>("init"));

        var result = await app.RunAsync(["init"]);

        Assert.Equal(0, result);

        var railwayPath = Path.Combine(_tempDir, ".railway");
        Assert.True(Directory.Exists(railwayPath));
        Assert.True(Directory.Exists(Path.Combine(railwayPath, "pipelines")));
        Assert.True(File.Exists(Path.Combine(railwayPath, "railway.yaml")));

        var manifestContent = await File.ReadAllTextAsync(Path.Combine(railwayPath, "railway.yaml"));
        Assert.Contains("name:", manifestContent);
        Assert.Contains("pipelines:", manifestContent);
    }

    [Fact]
    public async Task Init_Idempotent_ReturnsReinitializedMessage()
    {
        var console = new TestConsole();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c => c.AddCommand<InitCommand>("init"));

        // First init
        await app.RunAsync(["init"]);

        // Second init
        var result = await app.RunAsync(["init"]);

        Assert.Equal(0, result);
        Assert.Contains("Reinitialized existing Railway repository", console.Output);
    }

    [Fact]
    public async Task Init_NotInGitRepo_ReturnsFatalError()
    {
        var nonGitDir = Path.Combine(Path.GetTempPath(), "railway_nogit_" + Guid.NewGuid());
        Directory.CreateDirectory(nonGitDir);
        Directory.SetCurrentDirectory(nonGitDir);

        try
        {
            var console = new TestConsole();
            var registrar = new TestTypeRegistrar(console);
            var app = new CommandApp(registrar);
            app.Configure(c => c.AddCommand<InitCommand>("init"));

            var result = await app.RunAsync(["init"]);

            Assert.Equal(1, result);
            Assert.Contains("fatal: not a git repository", console.Output);
        }
        finally
        {
            Directory.SetCurrentDirectory(_tempDir);
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
