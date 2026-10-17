using System.Diagnostics;

using Cli.Commands;

using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Orchestration;
using Railway.Engine.Orchestration.Interfaces;

using Spectre.Console.Cli;
using Spectre.Console.Testing;

using Tests.Infrastructure;

using Xunit;

namespace Tests.Integration;

/// <summary>
/// Phase 3 git-event tests: real <c>git commit</c>, branch switch, file-level checkout, merge and a
/// local push to a bare temp remote each fire exactly the pipelines subscribed to that event and
/// matching its filters, unrelated events fire nothing, and a failing pipeline never blocks git.
/// </summary>
/// <remarks>
/// Same hygiene as <c>HooksGitLifecycleTests</c>: <c>HOME</c>/<c>XDG_DATA_HOME</c> are redirected to a
/// temp directory (the detached grandchild resolves the database through them), <c>RAILWAY_HOOKS_NOTIFY=0</c>
/// kills desktop notifications, and <see cref="IHookLauncher"/> is pinned to <c>dotnet &lt;Cli.dll&gt;</c>
/// (the test host is <c>testhost</c>, not the CLI). Detached effects are observed through repo-local
/// <c>.railway/logs/*.log</c> files, polled with generous timeouts.
/// </remarks>
[Collection("Integration Tests")]
public class HooksGitEventsTests : IDisposable
{
    private const int PollTimeoutMs = 30_000;

    private const int SettleMs = 2_000;

    private static readonly TimeSpan GitSanityBound = TimeSpan.FromSeconds(25);

    private static readonly string CliDllPath = typeof(HooksSettings).Assembly.Location;

    private readonly IntegrationTestFixture _fixture;
    private readonly TestRailwayRepository _repo;
    private readonly string _originalDir;
    private readonly string _lockRoot;
    private readonly ScopedEnvironment _environment;

    public HooksGitEventsTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _originalDir = Directory.GetCurrentDirectory();
        _environment = ScopedEnvironment.CreateForHookTests();
        _repo = new TestRailwayRepository();
        _lockRoot = Path.Combine(Path.GetTempPath(), "railway_hooks_events_locks_" + Guid.NewGuid().ToString("N"));

        // Pin the unborn branch to 'main' so branch names are deterministic regardless of the host's
        // init.defaultBranch (git may default to master).
        RunGit("symbolic-ref", "HEAD", "refs/heads/main");
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
        registrar.RegisterInstance(typeof(HookPaths), new HookPaths(_lockRoot));
        registrar.RegisterInstance(
            typeof(IHookLauncher),
            new PinnedHookLauncher(new HookLaunchCommand("dotnet", [CliDllPath])));

        return registrar;
    }

    private async Task<int> RunCliAsync(params string[] args)
    {
        var console = TestConsoleFactory.Create();
        var app = new CommandApp(CreateRegistrar(console));
        app.Configure(Configure);

        return await app.RunAsync(args, TestContext.Current.CancellationToken);
    }

    private string LogsDirectory => Path.Combine(_repo.Path, ".railway", "logs");

    private (int ExitCode, TimeSpan Elapsed) RunGit(params string[] arguments) =>
        RunGitInDirectory(_repo.Path, arguments);

    private IReadOnlyList<string> ReadLogs()
    {
        if (!Directory.Exists(LogsDirectory))
        {
            return [];
        }

        var contents = new List<string>();

        foreach (var logFile in Directory.GetFiles(LogsDirectory, "*.log"))
        {
            try
            {
                contents.Add(File.ReadAllText(logFile));
            }
            catch (IOException)
            {
                // Concurrently written by a detached grandchild; ignore this round.
            }
        }

        return contents;
    }

    private async Task<string> WaitForLogAsync(Func<string, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(PollTimeoutMs);
        string? lastContent = null;

        while (DateTime.UtcNow < deadline)
        {
            foreach (var content in ReadLogs())
            {
                lastContent = content;

                if (predicate(content))
                {
                    return content;
                }
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"No hook log matched within {PollTimeoutMs} ms. Last seen log content:\n{lastContent ?? "<none>"}");
        return null!;
    }

    private static async Task SettleAsync() =>
        await Task.Delay(SettleMs, TestContext.Current.CancellationToken);

    private void CommitInitial()
    {
        File.WriteAllText(Path.Combine(_repo.Path, "file.txt"), "initial");
        RunGit("add", "file.txt");
        Assert.Equal(0, RunGit("commit", "-m", "initial").ExitCode);
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

    // ---------------------------------------------------------------------- pipelines

    private static string Pipeline(string triggerYaml, string run) =>
        $"name: build\ntriggers:\n{triggerYaml}\njobs:\n  build:\n    steps:\n      - name: step\n        run: {run}\n";

    private static string PostCommitPipeline(string run = "echo commit") => Pipeline("  - post-commit", run);

    private static string PostMergePipeline(string run = "echo merge") => Pipeline("  - post-merge", run);

    private static string PostCheckoutPipeline(string run = "echo checkout") => Pipeline("  - post-checkout", run);

    private static string PushBranchPipeline(string branch, string run = "echo push") =>
        Pipeline($"  - push:\n      branches: [{branch}]", run);

    private static string PushTagPipeline(string tag, string run = "echo push") =>
        Pipeline($"  - push:\n      tags: [\"{tag}\"]", run);

    // ---------------------------------------------------------------------- selector (unit-level)

    [Fact]
    public void Selector_PostCheckout_ExcludesCloneUnlessOptedIn()
    {
        var cloneContext = new HookEventContext(PreviousSha: new string('0', 40), NewSha: "abc", Flag: "1");

        var excluded = new Pipeline
        {
            Name = "p",
            Triggers = [new TriggerDefinition { Event = TriggerEvent.PostCheckout }],
        };

        var included = new Pipeline
        {
            Name = "p",
            Triggers = [new TriggerDefinition { Event = TriggerEvent.PostCheckout, IncludeClone = true }],
        };

        Assert.False(HookPipelineSelector.MatchesEvent(excluded, TriggerEvent.PostCheckout, cloneContext));
        Assert.True(HookPipelineSelector.MatchesEvent(included, TriggerEvent.PostCheckout, cloneContext));

        // A real branch switch (previous HEAD is not all zeros) fires regardless of the opt-in.
        var branchContext = new HookEventContext(PreviousSha: "1111111", NewSha: "2222222", Flag: "1");
        Assert.True(HookPipelineSelector.MatchesEvent(excluded, TriggerEvent.PostCheckout, branchContext));
    }

    // ---------------------------------------------------------------------- integration

    [Fact]
    public async Task Commit_FiresOnlyPostCommitPipelines()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX sh hook chain");

        _repo.WritePipeline("commit.yaml", PostCommitPipeline());
        _repo.WritePipeline("merge.yaml", PostMergePipeline());

        Assert.Equal(0, await RunCliAsync("hooks", "install", "--path", _repo.Path));

        CommitInitial();

        await WaitForLogAsync(content =>
            content.Contains("pipeline: commit.yaml", StringComparison.Ordinal)
            && content.Contains("event: post-commit (trigger=hook)", StringComparison.Ordinal));

        await SettleAsync();

        Assert.DoesNotContain(ReadLogs(), content => content.Contains("pipeline: merge.yaml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Checkout_BranchSwitchFires_ButFileCheckoutDoesNot()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX sh hook chain");

        _repo.WritePipeline("checkout.yaml", PostCheckoutPipeline());
        Assert.Equal(0, await RunCliAsync("hooks", "install", "--path", _repo.Path));

        CommitInitial();

        var (branchExit, branchElapsed) = RunGit("checkout", "-b", "feature");
        Assert.Equal(0, branchExit);
        Assert.True(branchElapsed < GitSanityBound, $"git checkout -b took {branchElapsed}");

        await WaitForLogAsync(content =>
            content.Contains("pipeline: checkout.yaml", StringComparison.Ordinal)
            && content.Contains("event: post-checkout (trigger=hook)", StringComparison.Ordinal)
            && content.Contains("flag=1", StringComparison.Ordinal));

        // A file-level checkout (flag=0) must not fire: modify a tracked file, then restore it.
        var logCountAfterBranch = ReadLogs().Count;
        File.WriteAllText(Path.Combine(_repo.Path, "file.txt"), "modified");

        var (fileExit, fileElapsed) = RunGit("checkout", "--", "file.txt");
        Assert.Equal(0, fileExit);
        Assert.True(fileElapsed < GitSanityBound, $"file checkout took {fileElapsed}");

        await SettleAsync();

        Assert.Equal(logCountAfterBranch, ReadLogs().Count);
    }

    [Fact]
    public async Task Merge_FiresPostMergePipelines()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX sh hook chain");

        _repo.WritePipeline("merge.yaml", PostMergePipeline());
        Assert.Equal(0, await RunCliAsync("hooks", "install", "--path", _repo.Path));

        CommitInitial();

        Assert.Equal(0, RunGit("checkout", "-b", "feature").ExitCode);
        File.WriteAllText(Path.Combine(_repo.Path, "feature.txt"), "feature");
        Assert.Equal(0, RunGit("add", "feature.txt").ExitCode);
        Assert.Equal(0, RunGit("commit", "-m", "feature commit").ExitCode);
        Assert.Equal(0, RunGit("checkout", "main").ExitCode);

        var (mergeExit, mergeElapsed) = RunGit("merge", "--no-ff", "feature", "-m", "merge feature");
        Assert.Equal(0, mergeExit);
        Assert.True(mergeElapsed < GitSanityBound, $"git merge took {mergeElapsed}");

        await WaitForLogAsync(content =>
            content.Contains("pipeline: merge.yaml", StringComparison.Ordinal)
            && content.Contains("event: post-merge (trigger=hook)", StringComparison.Ordinal)
            && content.Contains("is_squash=false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Push_LocalToBareRemote_FiresOnlyMatchingBranchAndTagFilters()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX sh hook chain");

        _repo.WritePipeline("branch.yaml", PushBranchPipeline("main"));
        _repo.WritePipeline("tag.yaml", PushTagPipeline("v*"));
        _repo.WritePipeline("other.yaml", PushBranchPipeline("no-such-branch"));

        Assert.Equal(0, await RunCliAsync("hooks", "install", "--path", _repo.Path));

        CommitInitial();

        var remotePath = Path.Combine(Path.GetTempPath(), "railway_bare_remote_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(remotePath);

        try
        {
            Assert.Equal(0, RunGitInDirectory(remotePath, "init", "--bare").ExitCode);
            Assert.Equal(0, RunGit("remote", "add", "origin", remotePath).ExitCode);

            var (branchPushExit, branchPushElapsed) = RunGit("push", "origin", "main");
            Assert.Equal(0, branchPushExit);
            Assert.True(branchPushElapsed < GitSanityBound, $"git push (branch) took {branchPushElapsed}");

            await WaitForLogAsync(content =>
                content.Contains("pipeline: branch.yaml", StringComparison.Ordinal)
                && content.Contains("event: push (trigger=hook)", StringComparison.Ordinal)
                && content.Contains("branch=main", StringComparison.Ordinal));

            Assert.Equal(0, RunGit("tag", "v1.0").ExitCode);

            var (tagPushExit, tagPushElapsed) = RunGit("push", "origin", "v1.0");
            Assert.Equal(0, tagPushExit);
            Assert.True(tagPushElapsed < GitSanityBound, $"git push (tag) took {tagPushElapsed}");

            await WaitForLogAsync(content =>
                content.Contains("pipeline: tag.yaml", StringComparison.Ordinal)
                && content.Contains("event: push (trigger=hook)", StringComparison.Ordinal)
                && content.Contains("tag=v1.0", StringComparison.Ordinal));

            await SettleAsync();

            Assert.DoesNotContain(ReadLogs(), content => content.Contains("pipeline: other.yaml", StringComparison.Ordinal));
            Assert.DoesNotContain(ReadLogs(), content => content.Contains("pipeline: tag.yaml", StringComparison.Ordinal) && content.Contains("branch=main", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(remotePath);
        }
    }

    [Fact]
    public async Task Push_ChainedForeignPrePushHook_ReceivesGitRefLinesOnStdin()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX sh hook chain");

        _repo.WritePipeline("branch.yaml", PushBranchPipeline("main"));

        // Pre-create a foreign pre-push hook (before install, so railway moves it aside and chains it)
        // that records the stdin it receives.
        var sentinelPath = Path.Combine(_repo.Path, "foreign-prepush-stdin");
        var foreignHookPath = Path.Combine(_repo.Path, ".git", "hooks", "pre-push");
        File.WriteAllText(
            foreignHookPath,
            $"#!/bin/sh\ncat > {HookScriptHelper.QuoteSh(sentinelPath)}\nexit 0\n");

        Assert.Equal(0, await RunCliAsync("hooks", "install", "--path", _repo.Path));

        CommitInitial();

        var remotePath = Path.Combine(Path.GetTempPath(), "railway_bare_remote_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(remotePath);

        try
        {
            Assert.Equal(0, RunGitInDirectory(remotePath, "init", "--bare").ExitCode);
            Assert.Equal(0, RunGit("remote", "add", "origin", remotePath).ExitCode);
            Assert.Equal(0, RunGit("push", "origin", "main").ExitCode);

            // The railway wrapper drains git's ref lines, so a chained foreign pre-push hook would see
            // empty stdin unless the managed hook buffered it. By the time the synchronous push returns
            // the foreign hook has run; it must have recorded the pushed branch ref.
            Assert.True(File.Exists(sentinelPath), "the chained foreign pre-push hook should have run");
            Assert.Contains("refs/heads/main", File.ReadAllText(sentinelPath), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(remotePath);
        }
    }

    [Fact]
    public async Task UnrelatedEvents_FireNothing()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX sh hook chain");

        // Only post-commit is subscribed, so no checkout/merge/push hooks are installed at all.
        _repo.WritePipeline("commit.yaml", PostCommitPipeline());
        Assert.Equal(0, await RunCliAsync("hooks", "install", "--path", _repo.Path));

        CommitInitial();

        Assert.Equal(0, RunGit("checkout", "-b", "feature").ExitCode);
        Assert.Equal(0, RunGit("checkout", "main").ExitCode);

        var remotePath = Path.Combine(Path.GetTempPath(), "railway_bare_remote_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(remotePath);

        try
        {
            Assert.Equal(0, RunGitInDirectory(remotePath, "init", "--bare").ExitCode);
            Assert.Equal(0, RunGit("remote", "add", "origin", remotePath).ExitCode);
            Assert.Equal(0, RunGit("push", "origin", "main").ExitCode);
        }
        finally
        {
            TryDeleteDirectory(remotePath);
        }

        await SettleAsync();

        Assert.DoesNotContain(ReadLogs(), content => content.Contains("event: post-checkout", StringComparison.Ordinal));
        Assert.DoesNotContain(ReadLogs(), content => content.Contains("event: post-merge", StringComparison.Ordinal));
        Assert.DoesNotContain(ReadLogs(), content => content.Contains("event: push", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailingPipelines_NeverBlockGit()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX sh hook chain");

        _repo.WritePipeline("fail-commit.yaml", PostCommitPipeline("exit 1"));
        _repo.WritePipeline("fail-merge.yaml", PostMergePipeline("exit 1"));
        _repo.WritePipeline("fail-checkout.yaml", PostCheckoutPipeline("exit 1"));
        _repo.WritePipeline("fail-push.yaml", Pipeline("  - push", "exit 1"));

        Assert.Equal(0, await RunCliAsync("hooks", "install", "--path", _repo.Path));

        File.WriteAllText(Path.Combine(_repo.Path, "file.txt"), "initial");
        Assert.Equal(0, RunGit("add", "file.txt").ExitCode);

        var (commitExit, commitElapsed) = RunGit("commit", "-m", "initial");
        Assert.Equal(0, commitExit);
        Assert.True(commitElapsed < GitSanityBound, $"git commit took {commitElapsed}");

        var (checkoutExit, checkoutElapsed) = RunGit("checkout", "-b", "feature");
        Assert.Equal(0, checkoutExit);
        Assert.True(checkoutElapsed < GitSanityBound, $"git checkout took {checkoutElapsed}");

        File.WriteAllText(Path.Combine(_repo.Path, "feature.txt"), "feature");
        Assert.Equal(0, RunGit("add", "feature.txt").ExitCode);
        Assert.Equal(0, RunGit("commit", "-m", "feature commit", "--no-verify").ExitCode);
        Assert.Equal(0, RunGit("checkout", "main").ExitCode);

        var (mergeExit, mergeElapsed) = RunGit("merge", "--no-ff", "feature", "-m", "merge feature");
        Assert.Equal(0, mergeExit);
        Assert.True(mergeElapsed < GitSanityBound, $"git merge took {mergeElapsed}");

        var remotePath = Path.Combine(Path.GetTempPath(), "railway_bare_remote_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(remotePath);

        try
        {
            Assert.Equal(0, RunGitInDirectory(remotePath, "init", "--bare").ExitCode);
            Assert.Equal(0, RunGit("remote", "add", "origin", remotePath).ExitCode);

            var (pushExit, pushElapsed) = RunGit("push", "origin", "main");
            Assert.Equal(0, pushExit);
            Assert.True(pushElapsed < GitSanityBound, $"git push took {pushElapsed}");
        }
        finally
        {
            TryDeleteDirectory(remotePath);
        }

        // The failing pipelines still ran (detached) and logged their failure.
        await WaitForLogAsync(content =>
            content.Contains("pipeline: fail-commit.yaml", StringComparison.Ordinal)
            && content.Contains("RunCompleted", StringComparison.Ordinal)
            && content.Contains("exit=1", StringComparison.Ordinal));
    }

    private (int ExitCode, TimeSpan Elapsed) RunGitInDirectory(string directory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        process.Start();
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        stopwatch.Stop();

        return (process.ExitCode, stopwatch.Elapsed);
    }

    // ---------------------------------------------------------------------- collaborators

    private sealed class PinnedHookLauncher : IHookLauncher
    {
        private readonly HookLaunchCommand _command;

        public PinnedHookLauncher(HookLaunchCommand command) => _command = command;

        public HookLaunchCommand GetLaunchCommand() => _command;
    }

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
            new(Path.Combine(Path.GetTempPath(), "railway_hooks_events_home_" + Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("HOME", _originalHome);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", _originalXdgDataHome);
            Environment.SetEnvironmentVariable("RAILWAY_HOOKS_NOTIFY", _originalNotify);

            TryDeleteDirectory(_tempHome);
        }
    }
}
