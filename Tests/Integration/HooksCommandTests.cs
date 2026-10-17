using Cli.Commands;
using Cli.Handlers.Interfaces;
using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Integration;

/// <summary>
/// End-to-end tests for the <c>railway hooks</c> command branch (task 1.6) against a real temp git
/// repository and a temp SQLite database. Runs use the non-detached path for determinism (no
/// <c>--detach</c> is passed) and keep lock files in a temp root.
/// </summary>
[Collection("Integration Tests")]
public class HooksCommandTests : IDisposable
{
    private readonly IntegrationTestFixture _fixture;
    private readonly TestRailwayRepository _repo;
    private readonly string _originalDir;
    private readonly string _lockRoot;

    public HooksCommandTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _originalDir = Directory.GetCurrentDirectory();
        _repo = new TestRailwayRepository();
        _lockRoot = Path.Combine(Path.GetTempPath(), "railway_hooks_cmd_locks_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        _repo.Dispose();

        if (Directory.Exists(_lockRoot))
        {
            Directory.Delete(_lockRoot, recursive: true);
        }
    }

    private TestTypeRegistrar CreateRegistrar(TestConsole console)
    {
        var registrar = new TestTypeRegistrar(console, _fixture.DbPath);

        // Keep the per-pipeline lock files out of the shared app-data directory.
        registrar.RegisterInstance(typeof(HookPaths), new HookPaths(_lockRoot));
        return registrar;
    }

    private static void Configure(IConfigurator configurator)
    {
        configurator.AddCommand<InitCommand>("init");

        configurator.AddBranch<HooksSettings>(
            "hooks",
            hooks =>
            {
                hooks.AddCommand<HooksInstallCommand>("install");
                hooks.AddCommand<HooksUninstallCommand>("uninstall");
                hooks.AddCommand<HooksStatusCommand>("status");
                hooks.AddCommand<HooksSyncCommand>("sync");
                hooks.AddCommand<HooksRunCommand>("run");
                hooks.AddCommand<HooksWatchCommand>("watch");
            });
    }

    private async Task<(int ExitCode, TestConsole Console)> RunAsync(params string[] args)
        => await RunAsync(_ => { }, args);

    private async Task<(int ExitCode, TestConsole Console)> RunAsync(Action<TestTypeRegistrar> configure, params string[] args)
    {
        var console = TestConsoleFactory.Create();
        var registrar = CreateRegistrar(console);
        configure(registrar);
        var app = new CommandApp(registrar);
        app.Configure(Configure);

        var exitCode = await app.RunAsync(args, TestContext.Current.CancellationToken);
        return (exitCode, console);
    }

    private async Task<(int ExitCode, TestConsole Console)> RunWithParseErrorHandlingAsync(params string[] args)
        => await RunWithParseErrorHandlingAsync(_ => { }, args);

    private async Task<(int ExitCode, TestConsole Console)> RunWithParseErrorHandlingAsync(Action<TestTypeRegistrar> configure, params string[] args)
    {
        var console = TestConsoleFactory.Create();
        var registrar = CreateRegistrar(console);
        configure(registrar);
        var app = new CommandApp(registrar);
        app.Configure(configurator =>
        {
            Configure(configurator);
            configurator.PropagateExceptions();
        });

        try
        {
            var exitCode = await app.RunAsync(args, TestContext.Current.CancellationToken);
            return (exitCode, console);
        }
        catch (Spectre.Console.Cli.CommandParseException ex)
        {
            AnsiConsole.WriteLine($"Error: {ex.Message}");
            return (1, console);
        }
    }

    private RailwayDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<RailwayDbContext>()
            .UseSqlite($"Data Source={_fixture.DbPath}/data.db;Default Timeout=30")
            .Options;

        return new RailwayDbContext(options);
    }

    private const string PostCommitPipeline = """
        name: build
        triggers:
          - post-commit
        jobs:
          build:
            steps:
              - name: echo
                run: echo "hook ran"
        """;

    private const string PushPipeline = """
        name: deploy
        triggers:
          - push
        jobs:
          deploy:
            steps:
              - name: echo
                run: echo "push ran"
        """;

    [Fact]
    public async Task Install_WritesHookWrapperAndGitignore_AndReportsSuccess()
    {
        _repo.WritePipeline("build.yaml", PostCommitPipeline);

        var (exitCode, console) = await RunAsync("hooks", "install", "--path", _repo.Path);

        Assert.Equal(0, exitCode);
        Assert.True(
            File.Exists(Path.Combine(_repo.Path, ".git", "hooks", "post-commit")),
            "git hook post-commit should be installed");
        Assert.True(
            File.Exists(Path.Combine(_repo.Path, ".railway", "hooks", "post-commit.sh")),
            "generated wrapper should be written");

        var hookContent = File.ReadAllText(Path.Combine(_repo.Path, ".git", "hooks", "post-commit"));
        Assert.Contains(HookScriptHelper.HookMarkerPrefix, hookContent);

        var gitignore = File.ReadAllText(Path.Combine(_repo.Path, ".gitignore"));
        Assert.Contains(HookScriptHelper.GitignoreBlockStart, gitignore);
        Assert.Contains(".railway/hooks/", gitignore);

        Assert.Contains("Hooks installed", console.Output);
    }

    [Fact]
    public async Task Install_NoGitTriggers_ReturnsExitCodeOne()
    {
        // No pipelines at all: install must fail loudly rather than report success.
        var (exitCode, console) = await RunAsync("hooks", "install", "--path", _repo.Path);

        Assert.Equal(1, exitCode);
        Assert.Contains("No git-triggering pipelines", console.Output);
    }

    [Fact]
    public async Task Run_PostCommit_RunsOnlySubscribedPipelines_AndRecordsHookTrigger()
    {
        _repo.WritePipeline("build.yaml", PostCommitPipeline);
        _repo.WritePipeline("deploy.yaml", PushPipeline);

        var (exitCode, console) = await RunAsync(
            "hooks", "run", "post-commit", "--path", _repo.Path, "--no-notify");

        Assert.Equal(0, exitCode);
        Assert.Contains("build.yaml", console.Output);
        Assert.DoesNotContain("deploy.yaml", console.Output);

        using var context = CreateDbContext();
        var repoPath = Path.GetFullPath(_repo.Path);
        var repository = context.Repositories.Single(r => r.Path == repoPath);

        var run = context.Runs
            .Where(r => r.RepositoryId == repository.Id && r.PipelineFileName == "build.yaml")
            .OrderByDescending(r => r.Id)
            .First();

        Assert.Equal("hook", run.Trigger);
        Assert.False(
            context.Runs.Any(r => r.RepositoryId == repository.Id && r.PipelineFileName == "deploy.yaml"),
            "the push-only pipeline must not run for post-commit");
    }

    [Fact]
    public async Task Run_UnknownEvent_ReturnsExitCodeOne()
    {
        var (exitCode, console) = await RunAsync("hooks", "run", "bogus", "--path", _repo.Path);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown hook event", console.Output);
    }

    [Fact]
    public async Task Status_ReportsInstalledThenMissingAfterEditingPipelineYaml()
    {
        _repo.WritePipeline("build.yaml", PostCommitPipeline);

        var (installExit, _) = await RunAsync("hooks", "install", "--path", _repo.Path);
        Assert.Equal(0, installExit);

        var (statusExit, statusConsole) = await RunAsync("hooks", "status", "--path", _repo.Path);
        Assert.Equal(0, statusExit);
        Assert.Contains("post-commit", statusConsole.Output);
        Assert.Contains("installed", statusConsole.Output);

        // Declare a new git trigger but do not re-install: status must surface the drift between the
        // declared triggers and the installed hooks (post-merge is now declared but not installed).
        _repo.WritePipeline("build.yaml", PostCommitPipeline.Replace("- post-commit", "- post-commit\n  - post-merge"));

        var (driftExit, driftConsole) = await RunAsync("hooks", "status", "--path", _repo.Path);
        Assert.Equal(0, driftExit);
        Assert.Contains("post-merge", driftConsole.Output);
        Assert.Contains("missing", driftConsole.Output);
    }

    [Fact]
    public async Task Status_ReportsDriftedAfterManagedHookFileIsEdited()
    {
        _repo.WritePipeline("build.yaml", PostCommitPipeline);

        var (installExit, _) = await RunAsync("hooks", "install", "--path", _repo.Path);
        Assert.Equal(0, installExit);

        // Append to the managed hook (marker preserved) so its content no longer matches what
        // install would write now -> drifted.
        var hookPath = Path.Combine(_repo.Path, ".git", "hooks", "post-commit");
        File.WriteAllText(hookPath, File.ReadAllText(hookPath) + "\n# tampered\n");

        var (statusExit, statusConsole) = await RunAsync("hooks", "status", "--path", _repo.Path);

        Assert.Equal(0, statusExit);
        Assert.Contains("drifted", statusConsole.Output);
    }

    [Fact]
    public async Task Uninstall_RemovesManagedArtifacts_AndCleanRepositoryReportsNothing()
    {
        _repo.WritePipeline("build.yaml", PostCommitPipeline);
        await RunAsync("hooks", "install", "--path", _repo.Path);

        var (uninstallExit, uninstallConsole) = await RunAsync("hooks", "uninstall", "--path", _repo.Path);

        Assert.Equal(0, uninstallExit);
        Assert.False(File.Exists(Path.Combine(_repo.Path, ".git", "hooks", "post-commit")));
        Assert.False(File.Exists(Path.Combine(_repo.Path, ".railway", "hooks", "post-commit.sh")));
        Assert.Contains("Hooks uninstalled", uninstallConsole.Output);

        var (cleanExit, cleanConsole) = await RunAsync("hooks", "uninstall", "--path", _repo.Path);
        Assert.Equal(0, cleanExit);
        Assert.Contains("Nothing to remove", cleanConsole.Output);
    }

    [Fact]
    public async Task Watch_InvalidDebounce_ReturnsExitCodeOneWithClearError()
    {
        // `hooks watch` now blocks in the foreground; the non-blocking CLI surface to assert here is
        // the --debounce validation (a bad value fails fast with exit 1). The watching behavior itself
        // is covered by HooksWatchTests.
        var (exitCode, console) = await RunAsync("hooks", "watch", "--debounce", "banana", "--path", _repo.Path);

        Assert.Equal(1, exitCode);
        Assert.Contains("Invalid --debounce", console.Output);
    }

    private sealed class RecordingHooksWatchHandler : IHooksWatchHandler
    {
        public string? PathReceived { get; private set; }

        public Task Handle(string? pipeline, TimeSpan? debounce, bool initial, string? path, bool verbose, CancellationToken ct = default)
        {
            PathReceived = path;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Watch_PositionalPath_BindsAndWatchesResolvedRepository()
    {
        var handler = new RecordingHooksWatchHandler();
        var (exitCode, _) = await RunAsync(
            registrar => registrar.RegisterInstance(typeof(IHooksWatchHandler), handler),
            "hooks", "watch", _repo.Path);

        Assert.Equal(0, exitCode);
        Assert.Equal(_repo.Path, handler.PathReceived);
    }

    [Fact]
    public async Task Watch_ConflictingPathAndPositionalPath_ReturnsExitCodeOne()
    {
        var otherPath = Path.Combine(_lockRoot, "other");
        Directory.CreateDirectory(otherPath);

        var (exitCode, console) = await RunAsync("hooks", "watch", "--path", _repo.Path, otherPath);
        Assert.Equal(1, exitCode);
        Assert.Contains("Conflicting", console.Output);

        // Same paths should not conflict
        var handler = new RecordingHooksWatchHandler();
        var (exitCode2, _) = await RunAsync(
            registrar => registrar.RegisterInstance(typeof(IHooksWatchHandler), handler),
            "hooks", "watch", "--path", _repo.Path, _repo.Path);
        Assert.Equal(0, exitCode2);
    }

    [Fact]
    public async Task Sync_IsIdempotentAfterInstall()
    {
        _repo.WritePipeline("build.yaml", PostCommitPipeline);
        await RunAsync("hooks", "install", "--path", _repo.Path);

        var (exitCode, console) = await RunAsync("hooks", "sync", "--path", _repo.Path);

        Assert.Equal(0, exitCode);
        Assert.Contains("in sync", console.Output);
    }
}
