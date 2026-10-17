using Cli.Commands;
using Microsoft.EntityFrameworkCore;
using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Integration;

/// <summary>
/// End-to-end tests for the Phase 2 scheduler path of <c>railway hooks</c>: install, status (including
/// drift after editing the cron), sync and uninstall run against the <c>fake</c> scheduler (selected
/// through <c>RAILWAY_HOOKS_SCHEDULER</c>) with its store rooted at a temp directory, so no real OS
/// scheduler is ever touched. Asserts the exact JSON the fake persists.
/// </summary>
[Collection("Integration Tests")]
public class HooksScheduleTests : IDisposable
{
    private const string NightlyPipeline = """
        name: nightly
        triggers:
          - schedule: "0 3 * * *"
        jobs:
          build:
            steps:
              - name: echo
                run: echo "scheduled"
        """;

    private readonly IntegrationTestFixture _fixture;
    private readonly TestRailwayRepository _repo;
    private readonly string _originalDir;
    private readonly string _lockRoot;
    private readonly string _fakeSchedulerRoot;
    private readonly string? _originalScheduler;
    private readonly string? _originalFakeRoot;
    private readonly HookPaths _paths;

    public HooksScheduleTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _originalDir = Directory.GetCurrentDirectory();
        _repo = new TestRailwayRepository();
        _lockRoot = Path.Combine(Path.GetTempPath(), "railway_hooks_sched_locks_" + Guid.NewGuid().ToString("N"));
        _fakeSchedulerRoot = Path.Combine(Path.GetTempPath(), "railway_hooks_sched_fake_" + Guid.NewGuid().ToString("N"));
        _paths = new HookPaths(_lockRoot);

        _originalScheduler = Environment.GetEnvironmentVariable("RAILWAY_HOOKS_SCHEDULER");
        _originalFakeRoot = Environment.GetEnvironmentVariable("RAILWAY_HOOKS_FAKE_SCHEDULER_ROOT");

        Environment.SetEnvironmentVariable("RAILWAY_HOOKS_SCHEDULER", "fake");
        Environment.SetEnvironmentVariable("RAILWAY_HOOKS_FAKE_SCHEDULER_ROOT", _fakeSchedulerRoot);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        Environment.SetEnvironmentVariable("RAILWAY_HOOKS_SCHEDULER", _originalScheduler);
        Environment.SetEnvironmentVariable("RAILWAY_HOOKS_FAKE_SCHEDULER_ROOT", _originalFakeRoot);
        _repo.Dispose();

        foreach (var directory in new[] { _lockRoot, _fakeSchedulerRoot })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private string StorePath => Path.Combine(_fakeSchedulerRoot, _paths.GetRepositorySlug(Path.GetFullPath(_repo.Path)) + ".json");

    private TestTypeRegistrar CreateRegistrar(TestConsole console)
    {
        var registrar = new TestTypeRegistrar(console, _fixture.DbPath);
        registrar.RegisterInstance(typeof(HookPaths), new HookPaths(_lockRoot));
        return registrar;
    }

    private static void Configure(IConfigurator configurator)
    {
        configurator.AddBranch<HooksSettings>(
            "hooks",
            hooks =>
            {
                hooks.AddCommand<HooksInstallCommand>("install");
                hooks.AddCommand<HooksUninstallCommand>("uninstall");
                hooks.AddCommand<HooksStatusCommand>("status");
                hooks.AddCommand<HooksSyncCommand>("sync");
                hooks.AddCommand<HooksRunCommand>("run");
            });
    }

    private RailwayDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<RailwayDbContext>()
            .UseSqlite($"Data Source={_fixture.DbPath}/data.db")
            .Options;

        return new RailwayDbContext(options);
    }

    private async Task<(int ExitCode, TestConsole Console)> RunAsync(params string[] args)
    {
        var console = TestConsoleFactory.Create();
        var registrar = CreateRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(Configure);

        var exitCode = await app.RunAsync(args, TestContext.Current.CancellationToken);
        return (exitCode, console);
    }

    [Fact]
    public async Task Install_MaterializesScheduleInFakeStore_AndStatusReportsItInstalled()
    {
        _repo.WritePipeline("nightly.yaml", NightlyPipeline);

        var (installExit, _) = await RunAsync("hooks", "install", "--path", _repo.Path);

        Assert.Equal(0, installExit);
        Assert.True(File.Exists(StorePath), "the fake scheduler store should exist");
        Assert.Equal(
            "[\n  {\n    \"PipelineFileName\": \"nightly.yaml\",\n    \"Cron\": \"0 3 * * *\"\n  }\n]",
            File.ReadAllText(StorePath));

        var (statusExit, statusConsole) = await RunAsync("hooks", "status", "--path", _repo.Path);
        Assert.Equal(0, statusExit);
        Assert.Contains("Scheduler: fake", statusConsole.Output);
        Assert.Contains("0 3 * * *", statusConsole.Output);
        Assert.Contains("installed", statusConsole.Output);
    }

    [Fact]
    public async Task Status_ReportsDrifted_AfterTheCronExpressionIsEdited()
    {
        _repo.WritePipeline("nightly.yaml", NightlyPipeline);
        await RunAsync("hooks", "install", "--path", _repo.Path);

        _repo.WritePipeline("nightly.yaml", NightlyPipeline.Replace("0 3 * * *", "0 4 * * *", StringComparison.Ordinal));

        var (exitCode, console) = await RunAsync("hooks", "status", "--path", _repo.Path);

        Assert.Equal(0, exitCode);
        Assert.Contains("drifted", console.Output);
        Assert.Contains("0 4 * * *", console.Output);
    }

    [Fact]
    public async Task Sync_ReplacesTheStaleScheduleWithTheDeclaredOne()
    {
        _repo.WritePipeline("nightly.yaml", NightlyPipeline);
        await RunAsync("hooks", "install", "--path", _repo.Path);

        _repo.WritePipeline("nightly.yaml", NightlyPipeline.Replace("0 3 * * *", "0 4 * * *", StringComparison.Ordinal));

        var (exitCode, console) = await RunAsync("hooks", "sync", "--path", _repo.Path);

        Assert.Equal(0, exitCode);
        var store = File.ReadAllText(StorePath);
        Assert.Contains("0 4 * * *", store);
        Assert.DoesNotContain("0 3 * * *", store);
        Assert.Contains("Hooks synced", console.Output);
    }

    [Fact]
    public async Task Uninstall_RemovesTheScheduleFromTheFakeStore()
    {
        _repo.WritePipeline("nightly.yaml", NightlyPipeline);
        await RunAsync("hooks", "install", "--path", _repo.Path);
        Assert.True(File.Exists(StorePath));

        var (exitCode, console) = await RunAsync("hooks", "uninstall", "--path", _repo.Path);

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(StorePath), "uninstall should drop the owned schedule entry");
        Assert.Contains("Hooks uninstalled", console.Output);
    }

    [Fact]
    public async Task Install_InvalidCron_FailsLoudlyWithNonZeroExitCode()
    {
        _repo.WritePipeline("nightly.yaml", NightlyPipeline.Replace("0 3 * * *", "not-a-cron", StringComparison.Ordinal));

        var (exitCode, console) = await RunAsync("hooks", "install", "--path", _repo.Path);

        Assert.Equal(1, exitCode);
        Assert.Contains("nightly.yaml", console.Output);
        Assert.Contains("not-a-cron", console.Output);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public async Task Install_CreatesTheLogsDirectoryUsedBySchedulerRedirects()
    {
        _repo.WritePipeline("nightly.yaml", NightlyPipeline);

        var (exitCode, _) = await RunAsync("hooks", "install", "--path", _repo.Path);

        Assert.Equal(0, exitCode);
        Assert.True(
            Directory.Exists(Path.Combine(_repo.Path, ".railway", "logs")),
            "scheduler entries redirect into .railway/logs, so install must create it");
    }

    [Fact]
    public async Task Sync_WhenScheduleMaterializationFails_ExitsNonZero()
    {
        _repo.WritePipeline("nightly.yaml", NightlyPipeline.Replace("0 3 * * *", "not-a-cron", StringComparison.Ordinal));

        var (exitCode, console) = await RunAsync("hooks", "sync", "--path", _repo.Path);

        Assert.Equal(1, exitCode);
        Assert.Contains("nightly.yaml", console.Output);
        Assert.Contains("not-a-cron", console.Output);
        Assert.Contains("Schedule sync failed", console.Output);
    }

    [Fact]
    public async Task Run_Schedule_AcceptsCronOptionAndRecordsItAsProvenance()
    {
        _repo.WritePipeline("nightly.yaml", NightlyPipeline);

        var (exitCode, console) = await RunAsync(
            "hooks", "run", "schedule",
            "--pipeline", "nightly.yaml",
            "--cron", "0 3 * * *",
            "--path", _repo.Path,
            "--no-notify");

        Assert.Equal(0, exitCode);
        Assert.Contains("nightly.yaml", console.Output);

        using var context = CreateDbContext();
        var repositoryPath = Path.GetFullPath(_repo.Path);
        var repository = context.Repositories.Single(candidate => candidate.Path == repositoryPath);
        var run = context.Runs
            .Where(candidate => candidate.RepositoryId == repository.Id && candidate.PipelineFileName == "nightly.yaml")
            .OrderByDescending(candidate => candidate.Id)
            .First();

        Assert.Equal("schedule", run.Trigger);
        Assert.Equal("cron=0 3 * * *", run.TriggerDetail);
    }
}
