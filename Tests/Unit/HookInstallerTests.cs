using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Railway.Engine.Orchestration;
using Railway.Engine.Orchestration.Interfaces;

using Tests.Infrastructure;

using Xunit;

namespace Tests.Unit;

public sealed class HookInstallerTests : IDisposable
{
    private readonly string _lockRoot = Path.Combine(Path.GetTempPath(), "railway_installer_locks_" + Guid.NewGuid().ToString("N"));
    private readonly List<TempGitRepo> _repositories = [];

    public void Dispose()
    {
        foreach (var repository in _repositories)
        {
            repository.Dispose();
        }

        if (Directory.Exists(_lockRoot))
        {
            Directory.Delete(_lockRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Install_WritesWrapperAndGitHooksThatAreExecutableAndPosix()
    {
        var repository = CreateRepository();
        var installer = CreateInstaller(repository, Candidate("build.yaml", TriggerEvent.PostCommit));

        var result = await installer.Install(repository.Path);

        var wrapperPath = Path.Combine(repository.Path, ".railway", "hooks", "post-commit.sh");
        Assert.True(File.Exists(wrapperPath));

        var wrapper = File.ReadAllText(wrapperPath);
        Assert.StartsWith("#!/bin/sh\n", wrapper);
        Assert.Contains("# railway-managed hook: post-commit", wrapper);
        Assert.Contains($"cd {HookScriptHelper.QuoteSh(repository.Path)} || exit 0", wrapper);
        Assert.Contains($"\"dotnet\" \"/app/Cli.dll\" hooks run post-commit --path {HookScriptHelper.QuoteSh(repository.Path)} --detach", wrapper);
        Assert.Contains("exit 0", wrapper);
        Assert.DoesNotContain("\r", wrapper);
        Assert.DoesNotContain("[[", wrapper);
        Assert.DoesNotContain("bash", wrapper);

        var hookPath = Path.Combine(repository.Path, ".git", "hooks", "post-commit");
        Assert.True(File.Exists(hookPath));

        var hook = File.ReadAllText(hookPath);
        Assert.Contains("# railway-managed hook: post-commit", hook);
        Assert.Contains($"sh {HookScriptHelper.QuoteSh(wrapperPath)} \"$@\"", hook);
        Assert.DoesNotContain(".railway-backup", hook);

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(wrapperPath).HasFlag(UnixFileMode.UserExecute));
            Assert.True(File.GetUnixFileMode(hookPath).HasFlag(UnixFileMode.UserExecute));
        }

        Assert.Contains(result.Artifacts, artifact => artifact.Kind == HookArtifactKind.Wrapper && artifact.Action == HookArtifactAction.Created);
        Assert.Contains(result.Artifacts, artifact => artifact.Kind == HookArtifactKind.GitHook && artifact.Action == HookArtifactAction.Created);
        Assert.True(result.GitignoreChanged);
    }

    [Fact]
    public async Task Install_IsIdempotent_IncludingTheSingleGitignoreBlock()
    {
        var repository = CreateRepository();
        File.WriteAllText(Path.Combine(repository.Path, ".gitignore"), "bin/\nobj/\n");

        var installer = CreateInstaller(
            repository,
            Candidate("build.yaml", TriggerEvent.PostCommit, TriggerEvent.PrePush, TriggerEvent.PostMerge));

        await installer.Install(repository.Path);
        var second = await installer.Install(repository.Path);

        var gitignore = File.ReadAllText(Path.Combine(repository.Path, ".gitignore"));
        Assert.Equal(1, CountOccurrences(gitignore, HookScriptHelper.GitignoreBlockStart));
        Assert.Contains("bin/", gitignore);
        Assert.Contains(".railway/hooks/", gitignore);
        Assert.Contains(".railway/logs/", gitignore);

        Assert.False(second.GitignoreChanged);
        Assert.All(second.Artifacts, artifact => Assert.Equal(HookArtifactAction.Unchanged, artifact.Action));
    }

    [Fact]
    public async Task Install_MaterializesPrePushPostMergePostCheckoutWrappersAndHooks()
    {
        var repository = CreateRepository();
        var installer = CreateInstaller(
            repository,
            Candidate("build.yaml", TriggerEvent.PrePush, TriggerEvent.PostMerge, TriggerEvent.PostCheckout));

        var result = await installer.Install(repository.Path);

        var expectations = new (TriggerEvent Event, string HookName, string Keyword)[]
        {
            (TriggerEvent.PrePush, "pre-push", "push"),
            (TriggerEvent.PostMerge, "post-merge", "post-merge"),
            (TriggerEvent.PostCheckout, "post-checkout", "post-checkout"),
        };

        foreach (var (triggerEvent, hookName, keyword) in expectations)
        {
            var wrapperPath = Path.Combine(repository.Path, ".railway", "hooks", $"{keyword}.sh");
            var hookPath = Path.Combine(repository.Path, ".git", "hooks", hookName);

            Assert.True(File.Exists(wrapperPath), $"wrapper {keyword}.sh should be written");
            Assert.True(File.Exists(hookPath), $"git hook {hookName} should be installed");

            var wrapper = File.ReadAllText(wrapperPath);
            Assert.Contains(HookScriptHelper.GetHookMarker(triggerEvent), wrapper);
            Assert.Contains($"hooks run {keyword}", wrapper);
            Assert.Contains("--detach", wrapper);
            Assert.EndsWith("exit 0\n", wrapper);
            Assert.DoesNotContain("\r", wrapper);

            // The git hook chains through the wrapper and preserves its (always 0) exit code.
            var hook = File.ReadAllText(hookPath);
            Assert.Contains(HookScriptHelper.GetHookMarker(triggerEvent), hook);
            Assert.Contains($"sh {HookScriptHelper.QuoteSh(wrapperPath)} \"$@\"", hook);

            Assert.Contains(
                result.Artifacts,
                artifact => artifact.Kind == HookArtifactKind.Wrapper
                    && artifact.Name == $"{keyword}.sh"
                    && artifact.Action == HookArtifactAction.Created);
            Assert.Contains(
                result.Artifacts,
                artifact => artifact.Kind == HookArtifactKind.GitHook
                    && artifact.Name == hookName
                    && artifact.Action == HookArtifactAction.Created);
        }
    }

    [Fact]
    public async Task Uninstall_RemovesPrePushPostMergePostCheckoutArtifacts()
    {
        var repository = CreateRepository();
        var installer = CreateInstaller(
            repository,
            Candidate("build.yaml", TriggerEvent.PrePush, TriggerEvent.PostMerge, TriggerEvent.PostCheckout));

        await installer.Install(repository.Path);
        var result = await installer.Uninstall(repository.Path);

        foreach (var (hookName, keyword) in new[] { ("pre-push", "push"), ("post-merge", "post-merge"), ("post-checkout", "post-checkout") })
        {
            Assert.False(File.Exists(Path.Combine(repository.Path, ".git", "hooks", hookName)));
            Assert.False(File.Exists(Path.Combine(repository.Path, ".railway", "hooks", $"{keyword}.sh")));
        }

        Assert.Contains(result.Artifacts, artifact => artifact.Kind == HookArtifactKind.GitHook && artifact.Name == "pre-push" && artifact.Action == HookArtifactAction.Removed);
        Assert.Contains(result.Artifacts, artifact => artifact.Kind == HookArtifactKind.GitHook && artifact.Name == "post-merge" && artifact.Action == HookArtifactAction.Removed);
        Assert.Contains(result.Artifacts, artifact => artifact.Kind == HookArtifactKind.GitHook && artifact.Name == "post-checkout" && artifact.Action == HookArtifactAction.Removed);
    }

    [Fact]
    public async Task Install_ForeignHook_IsMovedToBackupAndChainCalled()
    {
        var repository = CreateRepository();
        var hookPath = Path.Combine(repository.Path, ".git", "hooks", "post-commit");
        const string foreign = "#!/bin/sh\necho foreign hook\n";
        File.WriteAllText(hookPath, foreign);

        var installer = CreateInstaller(repository, Candidate("build.yaml", TriggerEvent.PostCommit));

        var result = await installer.Install(repository.Path);

        var backupPath = hookPath + HookScriptHelper.BackupSuffix;
        Assert.True(File.Exists(backupPath));
        Assert.Equal(foreign, File.ReadAllText(backupPath));

        var hook = File.ReadAllText(hookPath);
        Assert.Contains("# railway-managed hook: post-commit", hook);
        Assert.Contains($"if [ -f {HookScriptHelper.QuoteSh(backupPath)} ]; then", hook);
        Assert.Contains($"  sh {HookScriptHelper.QuoteSh(backupPath)} \"$@\"", hook);

        Assert.Contains(
            result.Artifacts,
            artifact => artifact.Kind == HookArtifactKind.GitHook
                && artifact.Action == HookArtifactAction.Chained
                && artifact.Detail == backupPath);
    }

    [Fact]
    public async Task Uninstall_RestoresBackupAndRemovesExactlyTheGitignoreBlock()
    {
        var repository = CreateRepository();
        var hookPath = Path.Combine(repository.Path, ".git", "hooks", "post-commit");
        const string foreign = "#!/bin/sh\necho foreign hook\n";
        File.WriteAllText(hookPath, foreign);

        const string originalGitignore = "bin/\nobj/\n";
        File.WriteAllText(Path.Combine(repository.Path, ".gitignore"), originalGitignore);

        var installer = CreateInstaller(repository, Candidate("build.yaml", TriggerEvent.PostCommit));
        await installer.Install(repository.Path);

        var result = await installer.Uninstall(repository.Path);

        Assert.Equal(foreign, File.ReadAllText(hookPath));
        Assert.False(File.Exists(hookPath + HookScriptHelper.BackupSuffix));
        Assert.False(File.Exists(Path.Combine(repository.Path, ".railway", "hooks", "post-commit.sh")));
        Assert.False(Directory.Exists(Path.Combine(repository.Path, ".railway", "hooks")));
        Assert.Equal(originalGitignore, File.ReadAllText(Path.Combine(repository.Path, ".gitignore")));

        Assert.Contains(result.Artifacts, artifact => artifact.Kind == HookArtifactKind.GitHook && artifact.Action == HookArtifactAction.Restored);
        Assert.Contains(result.Artifacts, artifact => artifact.Kind == HookArtifactKind.Wrapper && artifact.Action == HookArtifactAction.Removed);
        Assert.Contains(result.Artifacts, artifact => artifact.Kind == HookArtifactKind.Gitignore && artifact.Action == HookArtifactAction.Removed);
        Assert.True(result.AnyRemoved);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Uninstall_DeletesTheGitignoreItCreated()
    {
        var repository = CreateRepository();
        var installer = CreateInstaller(repository, Candidate("build.yaml", TriggerEvent.PostCommit));
        await installer.Install(repository.Path);

        Assert.True(File.Exists(Path.Combine(repository.Path, ".gitignore")));

        await installer.Uninstall(repository.Path);

        Assert.False(File.Exists(Path.Combine(repository.Path, ".gitignore")));
    }

    [Fact]
    public async Task Uninstall_WarnsAboutAndKeepsAForeignManagedHookItDoesNotOwn()
    {
        var repository = CreateRepository();
        var foreignPath = Path.Combine(repository.Path, ".git", "hooks", "post-commit");
        const string foreign = "#!/bin/sh\necho user hook\n";
        File.WriteAllText(foreignPath, foreign);

        var installer = CreateInstaller(repository, Candidate("build.yaml", TriggerEvent.PostCommit));

        var result = await installer.Uninstall(repository.Path);

        Assert.Equal(foreign, File.ReadAllText(foreignPath));
        Assert.Contains(result.Warnings, warning => warning.Contains("post-commit"));
        Assert.False(result.AnyRemoved);
    }

    [Fact]
    public async Task Uninstall_Purge_DeletesLogsOnlyWhenRequested()
    {
        var repository = CreateRepository();
        var installer = CreateInstaller(repository, Candidate("build.yaml", TriggerEvent.PostCommit));
        await installer.Install(repository.Path);

        var logsDirectory = Path.Combine(repository.Path, ".railway", "logs");
        Directory.CreateDirectory(logsDirectory);
        File.WriteAllText(Path.Combine(logsDirectory, "run.log"), "log");

        var kept = await installer.Uninstall(repository.Path, purge: false);
        Assert.False(kept.PurgedLogs);
        Assert.True(Directory.Exists(logsDirectory));

        var purged = await installer.Uninstall(repository.Path, purge: true);
        Assert.True(purged.PurgedLogs);
        Assert.False(Directory.Exists(logsDirectory));
    }

    [Fact]
    public async Task Status_ReportsInstalledForeignChainedDriftedAndMissing()
    {
        var repository = CreateRepository();
        var installer = CreateInstaller(
            repository,
            Candidate("build.yaml", TriggerEvent.PostCommit, TriggerEvent.PostMerge, TriggerEvent.PostCheckout));

        File.WriteAllText(
            Path.Combine(repository.Path, ".git", "hooks", "post-checkout"),
            "#!/bin/sh\necho user checkout\n");

        await installer.Install(repository.Path);

        var status = await installer.Status(repository.Path);
        var pipeline = Assert.Single(status.Pipelines);

        Assert.Equal(GitHookState.Installed, StateFor(pipeline, TriggerEvent.PostCommit));
        Assert.Equal(GitHookState.ForeignChained, StateFor(pipeline, TriggerEvent.PostCheckout));
        Assert.True(status.GitignoreConfigured);
        Assert.NotNull(status.GitHooksDirectory);

        // Tamper with the managed post-merge hook so it no longer matches what we would generate.
        var mergeHookPath = Path.Combine(repository.Path, ".git", "hooks", "post-merge");
        File.WriteAllText(mergeHookPath, "# railway-managed hook: post-merge\nexit 0\n");
        Assert.Equal(GitHookState.Drifted, StateFor(await installer.Status(repository.Path), TriggerEvent.PostMerge));

        File.Delete(mergeHookPath);
        Assert.Equal(GitHookState.Missing, StateFor(await installer.Status(repository.Path), TriggerEvent.PostMerge));
    }

    [Fact]
    public async Task Status_ReportsForeignHookThatWeHaveNotInstalled()
    {
        var repository = CreateRepository();
        var installer = CreateInstaller(repository, Candidate("build.yaml", TriggerEvent.PostCommit));
        File.WriteAllText(Path.Combine(repository.Path, ".git", "hooks", "post-commit"), "#!/bin/sh\necho user\n");

        var status = await installer.Status(repository.Path);
        var pipeline = Assert.Single(status.Pipelines);

        Assert.Equal(GitHookState.Foreign, StateFor(pipeline, TriggerEvent.PostCommit));
        Assert.False(status.GitignoreConfigured);
    }

    [Fact]
    public async Task Status_WithAnInvalidCron_DoesNotThrowAndReportsNoNextOccurrence()
    {
        var repository = CreateRepository();

        var pipeline = new Pipeline
        {
            Name = "build",
            Triggers = [new TriggerDefinition { Event = TriggerEvent.Schedule, Cron = "not-a-cron" }],
        };

        var installer = CreateInstaller(
            repository,
            new HookPipelineCandidate("build.yaml", repository.PipelinePath("build.yaml"), pipeline, []));

        // Before the fix, Status parsed the cron inline and threw on an invalid expression; it must
        // instead report a null next occurrence (the pipeline's Trigger.Errors already conveys the issue).
        var status = await installer.Status(repository.Path);
        var trigger = Assert.Single(Assert.Single(status.Pipelines).Triggers);

        Assert.Equal(TriggerEvent.Schedule, trigger.Event);
        Assert.Null(trigger.NextOccurrence);
    }

    [Fact]
    public async Task Status_ReportsTriggerErrorsLockStateNextOccurrenceAndLastRun()
    {
        using var database = new TestDatabaseFixture();
        var repository = CreateRepository();

        var pipeline = new Pipeline
        {
            Name = "build",
            Triggers =
            [
                new TriggerDefinition { Event = TriggerEvent.Schedule, Cron = "0 3 * * *" },
                new TriggerDefinition { Event = TriggerEvent.PrePush, Branches = ["main"], Errors = ["unknown option 'foo' for trigger 'push'"] },
            ],
        };

        var installer = CreateInstaller(
            repository,
            new HookPipelineCandidate("build.yaml", repository.PipelinePath("build.yaml"), pipeline, []),
            database);

        var paths = new HookPaths(_lockRoot);
        using var held = HookLock.TryAcquire(
            paths.LockRoot,
            paths.GetRepositorySlug(repository.Path),
            paths.GetPipelineSlug("build.yaml"));

        await SeedRunAsync(database, repository.Path, "build.yaml");

        var status = await installer.Status(repository.Path);
        var pipelineStatus = Assert.Single(status.Pipelines);

        var schedule = pipelineStatus.Triggers.Single(trigger => trigger.Event == TriggerEvent.Schedule);
        Assert.NotNull(schedule.NextOccurrence);
        Assert.Equal(3, schedule.NextOccurrence!.Value.Hour);

        var push = pipelineStatus.Triggers.Single(trigger => trigger.Event == TriggerEvent.PrePush);
        Assert.Equal(["unknown option 'foo' for trigger 'push'"], push.Errors);

        Assert.True(pipelineStatus.LockHeld);
        Assert.NotNull(pipelineStatus.LastRun);
        Assert.Equal("hook", pipelineStatus.LastRun!.Trigger);
        Assert.Equal(RunStatus.Succeeded, pipelineStatus.LastRun.Status);
    }

    [Fact]
    public async Task Sync_DropsStaleArtifactsAddsNewOnesAndReportsTheDiff()
    {
        var repository = CreateRepository();
        await CreateInstaller(repository, Candidate("build.yaml", TriggerEvent.PostCommit)).Install(repository.Path);

        Assert.True(File.Exists(Path.Combine(repository.Path, ".git", "hooks", "post-commit")));

        var result = await CreateInstaller(repository, Candidate("build.yaml", TriggerEvent.PostMerge)).Sync(repository.Path);

        Assert.False(File.Exists(Path.Combine(repository.Path, ".git", "hooks", "post-commit")));
        Assert.False(File.Exists(Path.Combine(repository.Path, ".railway", "hooks", "post-commit.sh")));
        Assert.True(File.Exists(Path.Combine(repository.Path, ".git", "hooks", "post-merge")));
        Assert.True(File.Exists(Path.Combine(repository.Path, ".railway", "hooks", "post-merge.sh")));

        Assert.Contains("git hook post-merge", result.Added);
        Assert.Contains("git hook post-commit", result.Removed);
        Assert.Contains("wrapper post-commit.sh", result.Removed);
    }

    [Fact]
    public async Task Sync_WhenStaleHookHadBackup_RestoresTheBackup()
    {
        var repository = CreateRepository();
        var hookPath = Path.Combine(repository.Path, ".git", "hooks", "post-commit");
        const string foreign = "#!/bin/sh\necho foreign\n";
        File.WriteAllText(hookPath, foreign);

        await CreateInstaller(repository, Candidate("build.yaml", TriggerEvent.PostCommit)).Install(repository.Path);
        await CreateInstaller(repository, Candidate("build.yaml", TriggerEvent.PostMerge)).Sync(repository.Path);

        Assert.Equal(foreign, File.ReadAllText(hookPath));
        Assert.False(File.Exists(hookPath + HookScriptHelper.BackupSuffix));
    }

    [Fact]
    public async Task Sync_WithOrphanBackupAndNoMatchingHook_LeavesItAndDoesNotThrow()
    {
        var repository = CreateRepository();
        var backupPath = Path.Combine(repository.Path, ".git", "hooks", "post-commit" + HookScriptHelper.BackupSuffix);
        File.WriteAllText(backupPath, "#!/bin/sh\necho orphan\n");

        // A backup-suffixed entry with no matching hook must be skipped, never read on its own.
        var result = await CreateInstaller(repository, Candidate("build.yaml", TriggerEvent.PostMerge)).Sync(repository.Path);

        Assert.True(File.Exists(backupPath), "the orphan backup should be left untouched");
        Assert.False(File.Exists(Path.Combine(repository.Path, ".git", "hooks", "post-commit")));
        Assert.DoesNotContain(result.Removed, entry => entry.Contains(HookScriptHelper.BackupSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sync_ReportsUpdatedWhenWrapperDrifted()
    {
        var repository = CreateRepository();
        await CreateInstaller(repository, Candidate("build.yaml", TriggerEvent.PostCommit)).Install(repository.Path);

        var wrapperPath = Path.Combine(repository.Path, ".railway", "hooks", "post-commit.sh");
        File.WriteAllText(wrapperPath, "# railway-managed hook: post-commit\nexit 0\n");

        var result = await CreateInstaller(repository, Candidate("build.yaml", TriggerEvent.PostCommit)).Sync(repository.Path);

        Assert.Contains("wrapper post-commit.sh", result.Updated);
        Assert.Contains("hooks run post-commit", File.ReadAllText(wrapperPath));
    }

    [Fact]
    public async Task Install_ResolvesHooksDirectoryThroughGit()
    {
        var repository = CreateRepository();
        Directory.CreateDirectory(Path.Combine(repository.Path, "custom-hooks"));
        repository.RunGit("config", "core.hooksPath", "custom-hooks");

        await CreateInstaller(repository, Candidate("build.yaml", TriggerEvent.PostCommit)).Install(repository.Path);

        Assert.True(File.Exists(Path.Combine(repository.Path, "custom-hooks", "post-commit")));
        Assert.False(File.Exists(Path.Combine(repository.Path, ".git", "hooks", "post-commit")));
    }

    private static GitHookState? StateFor(PipelineHookStatus pipeline, TriggerEvent triggerEvent) =>
        pipeline.Triggers.Single(trigger => trigger.Event == triggerEvent).HookState;

    private static GitHookState? StateFor(HookStatusReport report, TriggerEvent triggerEvent) =>
        StateFor(report.Pipelines.Single(), triggerEvent);

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = content.IndexOf(value, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = content.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static async Task SeedRunAsync(TestDatabaseFixture database, string repositoryPath, string pipelineFileName)
    {
        await using var context = database.CreateContext();

        var repository = new Repository { Path = Path.GetFullPath(repositoryPath) };
        context.Repositories.Add(repository);
        await context.SaveChangesAsync();

        context.Runs.Add(new Run
        {
            RepositoryId = repository.Id,
            PipelineFileName = pipelineFileName,
            PipelineName = "build",
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            Status = RunStatus.Succeeded,
            Trigger = "hook",
            TriggerDetail = "branch=main",
        });

        await context.SaveChangesAsync();
    }

    private static HookPipelineCandidate Candidate(string fileName, params TriggerEvent[] events) =>
        new(
            fileName,
            Path.Combine(Path.GetTempPath(), fileName),
            new Pipeline
            {
                Name = Path.GetFileNameWithoutExtension(fileName),
                Triggers = [.. events.Select(triggerEvent => new TriggerDefinition { Event = triggerEvent })],
            },
            []);

    private TempGitRepo CreateRepository()
    {
        var repository = new TempGitRepo();
        _repositories.Add(repository);
        return repository;
    }

    private HookInstaller CreateInstaller(
        TempGitRepo repository,
        HookPipelineCandidate candidate,
        TestDatabaseFixture? database = null) =>
        new(
            new HookPaths(_lockRoot),
            new SystemCommandRunner(),
            new FakeLauncher(),
            new FakePipelineSource(candidate),
            database?.CreateContext());
}
