using System.Diagnostics;

using Cli.Commands;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Railway.Engine;
using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Railway.Engine.Orchestration.Interfaces;

using Spectre.Console.Cli;
using Spectre.Console.Testing;

using Tests.Infrastructure;

using Xunit;

namespace Tests.Integration;

/// <summary>
/// Phase 1 git-hook lifecycle tests (task 1.8) against a real temp git repository, real <c>git</c>
/// and the real CLI assembly: install artifacts + idempotency, a real <c>git commit</c> firing the
/// post-commit chain (non-blocking even for a failing pipeline), foreign-hook preservation/chaining
/// and uninstall restoration, skipped-run visibility while the hook lock is held (driving
/// <see cref="IHookRunner"/> directly — see the hygiene remark below), and
/// concurrent in-process runner invocations against one SQLite database.
/// </summary>
/// <remarks>
/// Hygiene constraints these tests are designed around:
/// <list type="bullet">
/// <item>
/// A real <c>git commit</c> fires a detached railway child (the wrapper passes <c>--detach</c>) whose
/// grandchild runs the pipeline in-process and resolves the database through
/// <see cref="DatabasePathHelper"/> — on macOS that is <c>~/Library/Application Support/Railway</c>
/// with no env override. <c>HOME</c> (and <c>XDG_DATA_HOME</c>) are therefore redirected to a temp
/// directory for the duration of each test so the grandchild never touches the developer's real
/// database (the collection is serialized, so process-wide env mutation is safe).
/// </item>
/// <item>
/// The test host's own process shape is <c>testhost</c>, so <c>EnvironmentHookLauncher</c> would embed
/// the wrong binary into generated wrappers. <c>IHookLauncher</c> is overridden in DI (last registration
/// wins after <c>AddEngine</c>) to pin <c>dotnet &lt;Cli.dll&gt;</c> using the CLI assembly copied into
/// the test output directory.
/// </item>
/// <item>
/// <c>RAILWAY_HOOKS_NOTIFY=0</c> suppresses all notifications (macOS osascript) for in-process and
/// out-of-process runs alike.
/// </item>
/// <item>
/// The lock-held and concurrency tests drive <see cref="IHookRunner"/> in-process (through the DI
/// container) rather than through the Spectre command: the assertions are about the recorded
/// <see cref="RunStatus.Skipped"/> row and lock contention, so spawning a CLI process would add
/// nondeterminism without covering anything new. The skipped branch of
/// <c>HooksRunHandler.RenderResult</c> renders "skipped" without throwing markup errors.
/// </item>
/// </list>
/// Assertions on the detached grandchild use repo-local artifacts (<c>.railway/logs/*.log</c>), polled
/// with generous timeouts — never fixed sleeps as the primary synchronization.
/// </summary>
[Collection("Integration Tests")]
public class HooksGitLifecycleTests : IDisposable
{
    private const int PollTimeoutMs = 30_000;

    private static readonly TimeSpan CommitSanityBound = TimeSpan.FromSeconds(25);

    private static readonly string CliDllPath = typeof(HooksSettings).Assembly.Location;

    private readonly IntegrationTestFixture _fixture;
    private readonly TestRailwayRepository _repo;
    private readonly string _originalDir;
    private readonly string _lockRoot;
    private readonly ScopedEnvironment _environment;

    public HooksGitLifecycleTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _originalDir = Directory.GetCurrentDirectory();

        // Redirect HOME/XDG_DATA_HOME and kill notifications BEFORE anything spawns processes, so
        // every child (git -> hook -> dotnet child -> detached grandchild) inherits the safe env.
        _environment = ScopedEnvironment.CreateForHookTests();

        _repo = new TestRailwayRepository();
        _lockRoot = Path.Combine(Path.GetTempPath(), "railway_hooks_life_locks_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        _repo.Dispose();
        _environment.Dispose();

        TryDeleteDirectory(_lockRoot);
    }

    // ---------------------------------------------------------------------- harness

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
                hooks.AddCommand<HooksWatchCommand>("watch");
            });
    }

    private TestTypeRegistrar CreateRegistrar(TestConsole console)
    {
        var registrar = new TestTypeRegistrar(console, _fixture.DbPath);

        // Keep lock files out of the shared app-data directory.
        registrar.RegisterInstance(typeof(HookPaths), new HookPaths(_lockRoot));

        // Pin the launcher to the real CLI assembly: under the test host, EnvironmentHookLauncher
        // would capture 'testhost' and the generated wrapper would invoke the wrong binary.
        registrar.RegisterInstance(
            typeof(IHookLauncher),
            new PinnedHookLauncher(new HookLaunchCommand("dotnet", [CliDllPath])));

        return registrar;
    }

    private async Task<(int ExitCode, TestConsole Console)> RunCliAsync(params string[] args)
    {
        var console = TestConsoleFactory.Create();
        var app = new CommandApp(CreateRegistrar(console));
        app.Configure(Configure);

        var exitCode = await app.RunAsync(args, TestContext.Current.CancellationToken);
        return (exitCode, console);
    }

    private RailwayDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<RailwayDbContext>()
            .UseSqlite($"Data Source={_fixture.DbPath}/data.db;Default Timeout=30")
            .Options;

        return new RailwayDbContext(options);
    }

    private string GitHookPath => Path.Combine(_repo.Path, ".git", "hooks", "post-commit");

    private string GitHookBackupPath => GitHookPath + HookScriptHelper.BackupSuffix;

    private string WrapperPath => Path.Combine(_repo.Path, ".railway", "hooks", "post-commit.sh");

    /// <summary>
    /// Writes <see cref="ForeignHookScript"/> to the post-commit path and returns its bytes, so a test
    /// can assert the exact original is preserved/chained/restored.
    /// </summary>
    private byte[] CreateForeignHook()
    {
        File.WriteAllText(GitHookPath, ForeignHookScript);
        return File.ReadAllBytes(GitHookPath);
    }

    /// <summary>
    /// Builds an Engine-only container against the fixture database (temp lock root, pinned
    /// launcher) for driving <see cref="IHookRunner"/> in-process.
    /// </summary>
    private ServiceProvider CreateEngineProvider()
    {
        var services = new ServiceCollection();
        services.AddEngine(new EngineOptions { DatabasePath = _fixture.DbPath });
        services.AddSingleton(new HookPaths(_lockRoot));
        services.AddSingleton<IHookLauncher>(new PinnedHookLauncher(new HookLaunchCommand("dotnet", [CliDllPath])));

        var provider = services.BuildServiceProvider();
        provider.InitializeEngineAsync().GetAwaiter().GetResult();
        return provider;
    }

    private (int ExitCode, TimeSpan Elapsed) RunGit(string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = _repo.Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        var stopwatch = Stopwatch.StartNew();
        process.Start();
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        stopwatch.Stop();

        return (process.ExitCode, stopwatch.Elapsed);
    }

    /// <summary>
    /// Polls <c>.railway/logs/</c> until some log file's content satisfies <paramref name="predicate"/>
    /// (the detached grandchild writes there asynchronously after the commit returns).
    /// </summary>
    private async Task<string> WaitForLogAsync(Func<string, bool> predicate)
    {
        var logsDirectory = Path.Combine(_repo.Path, ".railway", "logs");
        var deadline = DateTime.UtcNow.AddMilliseconds(PollTimeoutMs);
        string? lastContent = null;

        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(logsDirectory))
            {
                foreach (var logFile in Directory.GetFiles(logsDirectory, "*.log"))
                {
                    try
                    {
                        var content = File.ReadAllText(logFile);
                        lastContent = content;

                        if (predicate(content))
                        {
                            return content;
                        }
                    }
                    catch (IOException)
                    {
                        // Concurrently written by the grandchild; retry.
                    }
                }
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail(
            $"No post-commit log matched within {PollTimeoutMs} ms. Last seen log content:\n{lastContent ?? "<none>"}");
        return null!; // Assert.Fail throws.
    }

    /// <summary>
    /// Polls until <paramref name="path"/> exists (a background process creates it asynchronously).
    /// </summary>
    private static async Task<bool> WaitForFileAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(PollTimeoutMs);

        while (!File.Exists(path) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return File.Exists(path);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
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

    private const string FailingPostCommitPipeline = """
        name: build
        triggers:
          - post-commit
        jobs:
          build:
            steps:
              - name: boom
                run: exit 1
        """;

    private const string ForeignHookScript = "#!/bin/sh\ntouch \"$PWD/foreign-hook-ran\"\n";

    // ---------------------------------------------------------------------- tests

    [Fact]
    public async Task Install_WritesAllArtifacts_AndSecondInstallIsIdempotent()
    {
        _repo.WritePipeline("build.yaml", PostCommitPipeline);

        var (exitCode, console) = await RunCliAsync("hooks", "install", "--path", _repo.Path);

        Assert.Equal(0, exitCode);
        Assert.Contains("Hooks installed", console.Output);

        var gitignorePath = Path.Combine(_repo.Path, ".gitignore");

        // Git hook: marker line + delegation to the wrapper.
        Assert.True(File.Exists(GitHookPath), "git hook post-commit should be installed");
        var gitHookContent = File.ReadAllText(GitHookPath);
        Assert.Contains(HookScriptHelper.GetHookMarker(TriggerEvent.PostCommit), gitHookContent);
        Assert.Contains(HookScriptHelper.QuoteSh(WrapperPath), gitHookContent);

        // Wrapper: marker, pinned launcher, the CLI assembly, and the non-blocking --detach flag
        // with an unconditional 'exit 0' so a failing pipeline can never abort the commit.
        Assert.True(File.Exists(WrapperPath), "generated wrapper should be written");
        var wrapperContent = File.ReadAllText(WrapperPath);
        Assert.Contains(HookScriptHelper.GetHookMarker(TriggerEvent.PostCommit), wrapperContent);
        Assert.Contains("dotnet", wrapperContent);
        Assert.Contains(HookScriptHelper.QuoteSh(CliDllPath), wrapperContent);
        Assert.Contains(" hooks run post-commit --path ", wrapperContent);
        Assert.Contains("--detach", wrapperContent);
        Assert.EndsWith("exit 0\n", wrapperContent);

        // .gitignore: exactly one marker block covering wrappers and logs.
        var gitignore = File.ReadAllText(gitignorePath);
        Assert.Contains(HookScriptHelper.GitignoreBlockStart, gitignore);
        Assert.Contains(HookScriptHelper.GitignoreBlockEnd, gitignore);

        foreach (var entry in HookScriptHelper.GitignoreBlockEntries)
        {
            Assert.Contains(entry, gitignore);
        }

        Assert.Equal(
            1,
            gitignore.Split(HookScriptHelper.GitignoreBlockStart).Length - 1);

        // Second install: byte-identical artifacts, a single gitignore block, and the
        // already-installed report.
        var gitHookBytes = File.ReadAllBytes(GitHookPath);
        var wrapperBytes = File.ReadAllBytes(WrapperPath);

        var (secondExit, secondConsole) = await RunCliAsync("hooks", "install", "--path", _repo.Path);

        Assert.Equal(0, secondExit);
        Assert.Contains("already up to date", secondConsole.Output);
        Assert.Equal(gitHookBytes, File.ReadAllBytes(GitHookPath));
        Assert.Equal(wrapperBytes, File.ReadAllBytes(WrapperPath));

        var secondGitignore = File.ReadAllText(gitignorePath);
        Assert.Equal(
            1,
            secondGitignore.Split(HookScriptHelper.GitignoreBlockStart).Length - 1);
    }

    [Fact]
    public async Task RealCommit_FiresHook_NonBlocking_EvenWhenPipelineFails()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX sh hook chain");

        _repo.WritePipeline("build.yaml", FailingPostCommitPipeline);

        var (installExit, _) = await RunCliAsync("hooks", "install", "--path", _repo.Path);
        Assert.Equal(0, installExit);

        // Structural non-blocking guarantees: the generated wrapper passes --detach and always
        // exits 0, so git can never be blocked (or aborted) by the pipeline outcome.
        var wrapperContent = File.ReadAllText(WrapperPath);
        Assert.Contains("--detach", wrapperContent);
        Assert.EndsWith("exit 0\n", wrapperContent);

        // Trigger a REAL commit.
        File.WriteAllText(Path.Combine(_repo.Path, "trigger.txt"), "trigger");
        RunGit("add trigger.txt");
        var (commitExit, commitElapsed) = RunGit("commit -m trigger");

        // (a) The commit is never blocked or aborted by the hook chain.
        Assert.Equal(0, commitExit);

        // (c) The commit process itself returned promptly (sanity bound; the wrapper's --detach
        // flag above is the structural guarantee that the pipeline does not run inline).
        Assert.True(
            commitElapsed < CommitSanityBound,
            $"git commit took {commitElapsed}, expected well under {CommitSanityBound}");

        // (b) The pipeline actually ran (detached grandchild) and failed: poll the repo-local log
        // for the RunCompleted event with a failing exit code.
        var log = await WaitForLogAsync(content =>
            content.Contains("event: post-commit (trigger=hook)", StringComparison.Ordinal)
            && content.Contains("RunCompleted", StringComparison.Ordinal)
            && content.Contains("exit=1", StringComparison.Ordinal));

        Assert.Contains("pipeline: build.yaml", log);
    }

    [Fact]
    public async Task ForeignHook_IsPreservedChainedExecuted_OnRealCommit()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX sh hook chain");

        _repo.WritePipeline("build.yaml", PostCommitPipeline);

        // Pre-create a foreign post-commit hook that drops a sentinel at the repo root.
        var backupPath = GitHookBackupPath;
        var foreignBytes = CreateForeignHook();

        var (installExit, installConsole) = await RunCliAsync("hooks", "install", "--path", _repo.Path);

        Assert.Equal(0, installExit);
        Assert.Contains("chained", installConsole.Output);

        // The foreign hook was moved aside and the new hook chain-calls it.
        Assert.True(File.Exists(backupPath), "foreign hook should be preserved as .railway-backup");
        Assert.Equal(foreignBytes, File.ReadAllBytes(backupPath));
        var managedHook = File.ReadAllText(GitHookPath);
        Assert.Contains(HookScriptHelper.GetHookMarker(TriggerEvent.PostCommit), managedHook);
        Assert.Contains(HookScriptHelper.QuoteSh(backupPath), managedHook);

        // Real commit: the foreign sentinel must appear AND the railway run log must appear.
        File.WriteAllText(Path.Combine(_repo.Path, "trigger.txt"), "trigger");
        RunGit("add trigger.txt");
        var (commitExit, _) = RunGit("commit -m trigger");

        Assert.Equal(0, commitExit);

        var sentinelPath = Path.Combine(_repo.Path, "foreign-hook-ran");
        Assert.True(await WaitForFileAsync(sentinelPath), "the foreign hook should still be executed via the chain");

        await WaitForLogAsync(content =>
            content.Contains("event: post-commit (trigger=hook)", StringComparison.Ordinal)
            && content.Contains("RunCompleted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Uninstall_RestoresForeignHookRemovesArtifacts_AndIsIdempotent()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX sh hook chain");

        _repo.WritePipeline("build.yaml", PostCommitPipeline);

        var backupPath = GitHookBackupPath;
        var gitignorePath = Path.Combine(_repo.Path, ".gitignore");

        var foreignBytes = CreateForeignHook();

        var (installExit, _) = await RunCliAsync("hooks", "install", "--path", _repo.Path);
        Assert.Equal(0, installExit);

        var (uninstallExit, uninstallConsole) = await RunCliAsync("hooks", "uninstall", "--path", _repo.Path);

        Assert.Equal(0, uninstallExit);
        Assert.Contains("Hooks uninstalled", uninstallConsole.Output);

        // The original foreign hook is restored byte-for-byte; the backup is gone.
        Assert.True(File.Exists(GitHookPath), "foreign hook should be restored to its original path");
        Assert.Equal(foreignBytes, File.ReadAllBytes(GitHookPath));
        Assert.False(File.Exists(backupPath), "the .railway-backup should be consumed by the restore");

        // Wrapper removed.
        Assert.False(
            File.Exists(WrapperPath),
            "generated wrapper should be removed");

        // .gitignore marker block removed (the file itself is deleted when nothing else remains).
        Assert.False(File.Exists(gitignorePath) && File.ReadAllText(gitignorePath).Contains(
            HookScriptHelper.GitignoreBlockStart,
            StringComparison.Ordinal));

        // Second uninstall: idempotent — the restored foreign hook is not railway-managed, so it
        // is left in place (warning) and nothing else changes. Note: this path reports the warning
        // rather than "Nothing to remove" (which only happens on a repository with no hooks at all).
        var (secondExit, secondConsole) = await RunCliAsync("hooks", "uninstall", "--path", _repo.Path);

        Assert.Equal(0, secondExit);
        Assert.Contains("not managed by railway", secondConsole.Output);
        Assert.Equal(foreignBytes, File.ReadAllBytes(GitHookPath));
        Assert.False(File.Exists(backupPath));
        Assert.False(File.Exists(WrapperPath));

        // With no hooks left at all, uninstall reports the clean "nothing to remove" path.
        File.Delete(GitHookPath);
        var (cleanExit, cleanConsole) = await RunCliAsync("hooks", "uninstall", "--path", _repo.Path);

        Assert.Equal(0, cleanExit);
        Assert.Contains("Nothing to remove", cleanConsole.Output);
    }

    [Fact]
    public async Task Runner_LockHeld_RecordsSkippedRunWithoutJobsOrSteps()
    {
        _repo.WritePipeline("build.yaml", PostCommitPipeline);

        var paths = new HookPaths(_lockRoot);
        using var held = HookLock.TryAcquire(
            paths.LockRoot,
            paths.GetRepositorySlug(_repo.Path),
            paths.GetPipelineSlug("build.yaml"));

        Assert.NotNull(held);

        // Drive IHookRunner directly in-process (the lock-held branch of `hooks run`).
        using var provider = CreateEngineProvider();
        var runner = provider.GetRequiredService<IHookRunner>();

        var outcome = await runner.RunEventAsync(
            _repo.Path,
            TriggerEvent.PostCommit,
            new HookEventContext(),
            pipelineFile: null,
            detach: false,
            notify: false,
            TestContext.Current.CancellationToken);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Skipped);
        Assert.Equal(RunStatus.Skipped, result.Status);
        Assert.NotNull(result.RunId);

        var repoPath = Path.GetFullPath(_repo.Path);

        using var context = CreateDbContext();
        var repository = context.Repositories.Single(r => r.Path == repoPath);
        var run = context.Runs.Single(r => r.RepositoryId == repository.Id);

        Assert.Equal(RunStatus.Skipped, run.Status);
        Assert.Equal("hook", run.Trigger);
        Assert.NotNull(run.FinishedAt);
        Assert.Null(run.ExitCode);

        // No jobs and no steps were created for the skipped run (scoped to this run — the shared
        // fixture database legitimately contains job/step rows from other tests).
        var runJobIds = context.RunJobs.Where(job => job.RunId == run.Id).Select(job => job.Id).ToList();
        Assert.Empty(runJobIds);
        Assert.Empty(context.RunSteps.Where(step => runJobIds.Contains(step.RunJobId)).ToList());
    }

    [Fact]
    public async Task ConcurrentRunnerInvocations_DoNotCrash_OnSharedDatabase()
    {
        _repo.WritePipeline("build.yaml", PostCommitPipeline);

        // Two concurrent in-process invocations against the same SQLite database: they contend on
        // the (repo, pipeline) HookLock and on concurrent writes to the DB. Depending on timing one
        // run executes and the other records a Skipped row, or both execute sequentially after the
        // lock is released — every interleaving must complete without an exception
        // (Microsoft.Data.Sqlite busy-timeout absorbs write contention), and at least one real run
        // must have happened.
        using var firstProvider = CreateEngineProvider();
        using var secondProvider = CreateEngineProvider();

        var firstRunner = firstProvider.GetRequiredService<IHookRunner>();
        var secondRunner = secondProvider.GetRequiredService<IHookRunner>();

        var outcomes = await Task.WhenAll(
            firstRunner.RunEventAsync(
                _repo.Path,
                TriggerEvent.PostCommit,
                new HookEventContext(),
                pipelineFile: null,
                detach: false,
                notify: false,
                TestContext.Current.CancellationToken),
            secondRunner.RunEventAsync(
                _repo.Path,
                TriggerEvent.PostCommit,
                new HookEventContext(),
                pipelineFile: null,
                detach: false,
                notify: false,
                TestContext.Current.CancellationToken));

        Assert.All(outcomes, outcome => Assert.False(outcome.AnyFailed));
        Assert.Contains(outcomes, outcome => outcome.Results.Any(result => !result.Skipped));

        using var context = CreateDbContext();

        // Scope to this repository; with a unique index on Path and retry on constraint violations,
        // there should be exactly one repository row for the path.
        var repoPath = Path.GetFullPath(_repo.Path);
        var repositories = context.Repositories
            .Where(repository => repository.Path == repoPath)
            .ToList();
        Assert.Single(repositories);

        var repositoryId = repositories[0].Id;
        var runs = context.Runs
            .Where(run => run.RepositoryId == repositoryId && run.PipelineFileName == "build.yaml")
            .ToList();

        Assert.NotEmpty(runs);
        Assert.All(runs, run => Assert.Equal("hook", run.Trigger));
        Assert.Contains(runs, run => run.Status != RunStatus.Skipped);
    }

    // ---------------------------------------------------------------------- collaborators

    /// <summary>
    /// <see cref="IHookLauncher"/> pinned to an explicit command so generated wrappers invoke the
    /// real CLI assembly instead of the test host binary.
    /// </summary>
    private sealed class PinnedHookLauncher : IHookLauncher
    {
        private readonly HookLaunchCommand _command;

        public PinnedHookLauncher(HookLaunchCommand command) => _command = command;

        public HookLaunchCommand GetLaunchCommand() => _command;
    }

    /// <summary>
    /// Process-wide environment scoping: redirects HOME/XDG_DATA_HOME to a temp directory (so the
    /// detached hook grandchild's DatabasePathHelper resolution lands there, never in the real
    /// user app-data DB) and sets RAILWAY_HOOKS_NOTIFY=0 (no real desktop notifications). The
    /// integration collection is serialized, so mutating process env is safe.
    /// </summary>
    private sealed class ScopedEnvironment : IDisposable
    {
        private readonly string _tempHome;
        private readonly string? _originalHome;
        private readonly string? _originalXdgDataHome;
        private readonly string? _originalNotify;

        private ScopedEnvironment(string tempHome)
        {
            _tempHome = tempHome;

            _originalHome = Environment.GetEnvironmentVariable("HOME");
            _originalXdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            _originalNotify = Environment.GetEnvironmentVariable("RAILWAY_HOOKS_NOTIFY");

            Environment.SetEnvironmentVariable("HOME", tempHome);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(tempHome, ".local", "share"));
            Environment.SetEnvironmentVariable("RAILWAY_HOOKS_NOTIFY", "0");
        }

        public static ScopedEnvironment CreateForHookTests() =>
            new(Path.Combine(Path.GetTempPath(), "railway_hooks_life_home_" + Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("HOME", _originalHome);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", _originalXdgDataHome);
            Environment.SetEnvironmentVariable("RAILWAY_HOOKS_NOTIFY", _originalNotify);

            TryDeleteDirectory(_tempHome);
        }
    }
}
