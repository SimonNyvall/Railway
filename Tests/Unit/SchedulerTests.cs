using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Railway.Engine.Services.Interfaces;
using Railway.Engine.Services.Schedulers;

using Xunit;

namespace Tests.Unit;

/// <summary>
/// Unit tests for the Phase 2 scheduler seam: exact launchd plists, exact crontab blocks (with foreign
/// line preservation), exact <c>schtasks</c> argument vectors, loud failures for unmappable expressions,
/// selector environment override and reconcile add/remove behavior. Every machine interaction goes
/// through a recording <see cref="ISystemCommandRunner"/>; no real scheduler is ever touched.
/// </summary>
public sealed class SchedulerTests : IDisposable
{
    private static int _shortRepoSuffix;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "railway_sched_" + Guid.NewGuid().ToString("N"));
    private readonly string _launchdRoot;
    private readonly string _fakeRoot;
    private readonly string _repo;

    // schtasks' /TR value is capped at 262 chars; a long temp path would otherwise trip the guard, so
    // schtasks tests use a deliberately tiny repository path (the launcher + log path are long enough).
    private readonly string _shortRepo = Path.Combine(
        OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp",
        "r" + System.Threading.Interlocked.Increment(ref _shortRepoSuffix));
    private readonly HookPaths _paths;

    public SchedulerTests()
    {
        _launchdRoot = Path.Combine(_root, "LaunchAgents");
        _fakeRoot = Path.Combine(_root, "fake-store");
        _repo = Path.Combine(_root, "my-scheduler-repo");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(Path.Combine(_repo, ".railway", "pipelines"));
        Directory.CreateDirectory(_shortRepo);
        Directory.CreateDirectory(Path.Combine(_shortRepo, ".railway", "pipelines"));
        _paths = new HookPaths(Path.Combine(_root, "locks"));
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _root, _shortRepo })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // ----- repository slug (A5) --------------------------------------------------------------------

    [Fact]
    public void RepositorySlug_CombinesDirectoryNameWithAPathSpecificHash()
    {
        var sibling = Path.Combine(_root, "other-parent", new DirectoryInfo(_repo).Name);
        Directory.CreateDirectory(sibling);

        var slug = _paths.GetRepositorySlug(Absolute(_repo));
        var siblingSlug = _paths.GetRepositorySlug(Absolute(sibling));

        Assert.Equal("my-scheduler-repo", new DirectoryInfo(_repo).Name);
        Assert.StartsWith("my-scheduler-repo-", slug);
        Assert.Equal(8, slug.Split('-')[^1].Length);
        Assert.All(slug.Split('-')[^1], character => Assert.Contains(character, "0123456789abcdef"));

        // Same directory name, different absolute path: the hash keeps the two checkouts distinct
        // (a directory-name-only slug would collide here).
        Assert.NotEqual(slug, siblingSlug);
        Assert.Equal(slug, _paths.GetRepositorySlug(Absolute(_repo)));
    }

    // ----- launchd ---------------------------------------------------------------------------------

    [Fact]
    public void Install_Launchd_WritesExactPlistRunningWrapperAtTheScheduledTime()
    {
        var runner = LaunchdRunner();
        var scheduler = CreateLaunchd(runner);

        var outcome = scheduler.Install(_repo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        Assert.Equal(ScheduleAction.Created, outcome.Action);
        Assert.Null(outcome.Error);

        var repoSlug = _paths.GetRepositorySlug(Absolute(_repo));
        var hash = SchedulerBase.GetCronHash("0 3 * * *");
        var label = $"com.railway.{repoSlug}.build.{hash}";
        var plistPath = Path.Combine(_launchdRoot, label + ".plist");

        var wrapper = Path.Combine(Absolute(_repo), ".railway", "hooks", "schedule-build.sh");
        var log = Path.Combine(Absolute(_repo), ".railway", "logs", "schedule-build.log");

        Assert.Equal(plistPath, outcome.EntryName);
        Assert.True(File.Exists(plistPath));
        Assert.Equal(
            ExpectedPlist(label, wrapper, log, "build.yaml", "0 3 * * *", SingleEntry(0, 3)),
            File.ReadAllText(plistPath));

        // Walk through the launchctl interaction: resolve uid, then bootstrap gui/<uid> <plist>.
        Assert.Equal(("id", "[-u]"), Describe(runner.Calls[0]));
        Assert.Equal(("launchctl", $"[bootstrap gui/501 {plistPath}]"), Describe(runner.Calls[1]));
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public void Install_Launchd_CreatesTheLogDirectoryForTheRedirectTarget()
    {
        var logs = Path.Combine(Absolute(_repo), ".railway", "logs");
        Assert.False(Directory.Exists(logs));

        CreateLaunchd(LaunchdRunner()).Install(_repo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        Assert.True(Directory.Exists(logs));
    }

    [Fact]
    public void Install_Launchd_DayOfWeekList_ExpandsToTwoCalendarDicts()
    {
        var scheduler = CreateLaunchd(LaunchdRunner());

        scheduler.Install(_repo, new ScheduleRequest("build.yaml", "0 3 * * 1,5"));

        var repoSlug = _paths.GetRepositorySlug(Absolute(_repo));
        var hash = SchedulerBase.GetCronHash("0 3 * * 1,5");
        var plistPath = Path.Combine(_launchdRoot, $"com.railway.{repoSlug}.build.{hash}.plist");

        var content = File.ReadAllText(plistPath);
        Assert.Equal(2, CountOccurrences(content, "<key>DayOfWeek</key>"));
        Assert.Contains("<integer>1</integer>", content);
        Assert.Contains("<integer>5</integer>", content);
        Assert.DoesNotContain("<key>DayOfMonth</key>", content);
        Assert.Equal(2, CountOccurrences(content, "<key>Hour</key>"));
    }

    [Fact]
    public void Install_Launchd_DayOfMonthAndDayOfWeek_ExpandsToOrEntries()
    {
        var scheduler = CreateLaunchd(LaunchdRunner());

        // Cron semantics are dom OR dow: expansion must produce |dom| + |dow| = 3 dicts.
        scheduler.Install(_repo, new ScheduleRequest("build.yaml", "0 3 1,15 * 1"));

        var repoSlug = _paths.GetRepositorySlug(Absolute(_repo));
        var hash = SchedulerBase.GetCronHash("0 3 1,15 * 1");
        var plistPath = Path.Combine(_launchdRoot, $"com.railway.{repoSlug}.build.{hash}.plist");

        var content = File.ReadAllText(plistPath);
        Assert.Equal(3, CountOccurrences(content, "<dict>") - 1); // minus the top-level plist dict
        Assert.Equal(2, CountOccurrences(content, "<key>DayOfMonth</key>"));
        Assert.Equal(1, CountOccurrences(content, "<key>DayOfWeek</key>"));
    }

    [Fact]
    public void Install_Launchd_RunAtLoadIsFalse()
    {
        var scheduler = CreateLaunchd(LaunchdRunner());

        scheduler.Install(_repo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        var content = File.ReadAllText(Directory.EnumerateFiles(_launchdRoot, "*.plist").Single());
        Assert.Contains("<key>RunAtLoad</key>\n  <false/>", content);
    }

    [Fact]
    public void Install_Launchd_IsIdempotent_SecondRunReportsUnchanged()
    {
        var runner = LaunchdRunner();
        var scheduler = CreateLaunchd(runner);

        scheduler.Install(_repo, new ScheduleRequest("build.yaml", "0 3 * * *"));
        var callsAfterFirst = runner.Calls.Count;

        var second = scheduler.Install(_repo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        Assert.Equal(ScheduleAction.Unchanged, second.Action);
        Assert.Equal(callsAfterFirst, runner.Calls.Count); // no reload when nothing changed
    }

    [Fact]
    public void Install_Launchd_WhenLaunchctlUnavailable_WritesPlistAndWarns()
    {
        var runner = new RecordingSystemCommandRunner
        {
            Handler = (fileName, _, _) => fileName == "id"
                ? new SystemCommandResult(127, string.Empty, "id: not found")
                : new SystemCommandResult(0, string.Empty, string.Empty),
        };

        var outcome = CreateLaunchd(runner).Install(_repo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        Assert.Equal(ScheduleAction.Created, outcome.Action);
        Assert.NotNull(outcome.Warning);
        Assert.Contains("launchctl", outcome.Warning!);
        Assert.Single(Directory.EnumerateFiles(_launchdRoot, "*.plist"));
    }

    [Fact]
    public void Install_Launchd_InvalidExpression_FailsLoudlyNamingPipelineAndExpression()
    {
        var outcome = CreateLaunchd(LaunchdRunner()).Install(_repo, new ScheduleRequest("build.yaml", "not-a-cron"));

        Assert.Equal(ScheduleAction.Failed, outcome.Action);
        Assert.NotNull(outcome.Error);
        Assert.Contains("build.yaml", outcome.Error!);
        Assert.Contains("not-a-cron", outcome.Error!);
        Assert.Contains("launchd", outcome.Error!);
        Assert.False(Directory.Exists(_launchdRoot) && Directory.EnumerateFiles(_launchdRoot, "*.plist").Any());
    }

    [Fact]
    public void Inspect_Launchd_AfterInstall_ReportsInstalledThenDrifted()
    {
        var scheduler = CreateLaunchd(LaunchdRunner());
        scheduler.Install(_repo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        Assert.Equal(ScheduleState.Installed, scheduler.Inspect(_repo, new ScheduleRequest("build.yaml", "0 3 * * *")).State);

        // Keep the entry owned (marker + metadata intact) but change the recorded cron: drift.
        var plistPath = Directory.EnumerateFiles(_launchdRoot, "*.plist").Single();
        File.WriteAllText(
            plistPath,
            File.ReadAllText(plistPath).Replace("<string>0 3 * * *</string>", "<string>0 4 * * *</string>", StringComparison.Ordinal));

        Assert.Equal(ScheduleState.Drifted, scheduler.Inspect(_repo, new ScheduleRequest("build.yaml", "0 3 * * *")).State);
    }

    // ----- crontab ---------------------------------------------------------------------------------

    [Fact]
    public void Install_Crontab_PreservesForeignLinesAndWritesExactBlock()
    {
        var machine = new CrontabMachine("MAILTO=me\n0 5 * * * /usr/bin/backup\n");
        var scheduler = CreateCrontab(machine);

        var outcome = scheduler.Install(_repo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        Assert.Equal(ScheduleAction.Created, outcome.Action);

        var wrapper = Path.Combine(Absolute(_repo), ".railway", "hooks", "schedule-build.sh");
        var log = Path.Combine(Absolute(_repo), ".railway", "logs", "schedule-build.log");
        var repoSlug = _paths.GetRepositorySlug(Absolute(_repo));

        var expected = string.Join(
            '\n',
            "MAILTO=me",
            "0 5 * * * /usr/bin/backup",
            string.Empty,
            $"# railway: {repoSlug}:build begin",
            "# Generated by Railway. Do not edit; run 'railway hooks sync' to refresh.",
            "# pipeline: build.yaml",
            "# cron: 0 3 * * *",
            $"0 3 * * * /bin/sh \"{wrapper}\" \"0 3 * * *\" >> \"{log}\" 2>&1",
            $"# railway: {repoSlug}:build end") + "\n";

        Assert.Equal(expected, machine.Content);
        Assert.Equal(("crontab", "[-]"), Describe(machine.Runner.Calls[^1]));
        Assert.Equal(expected, machine.Runner.Calls[^1].Stdin);
    }

    [Fact]
    public void Install_Crontab_CreatesTheLogDirectoryForTheRedirectTarget()
    {
        var machine = new CrontabMachine("MAILTO=me\n");
        var logs = Path.Combine(Absolute(_repo), ".railway", "logs");
        Assert.False(Directory.Exists(logs));

        CreateCrontab(machine).Install(_repo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        Assert.True(Directory.Exists(logs));
    }

    [Fact]
    public void Install_Crontab_IsIdempotent_SecondRunReportsUnchanged()
    {
        var machine = new CrontabMachine("MAILTO=me\n");
        var scheduler = CreateCrontab(machine);

        scheduler.Install(_repo, new ScheduleRequest("build.yaml", "0 3 * * *"));
        var second = scheduler.Install(_repo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        Assert.Equal(ScheduleAction.Unchanged, second.Action);
        Assert.Equal(2, CountOccurrences(machine.Content, "# railway:")); // begin + end markers only
    }

    [Fact]
    public void Remove_Crontab_DropsOnlyTheOwnedBlock()
    {
        var machine = new CrontabMachine("MAILTO=me\n");
        var scheduler = CreateCrontab(machine);
        scheduler.Install(_repo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        var outcome = scheduler.Remove(_repo, "build.yaml", "0 3 * * *");

        Assert.Equal(ScheduleAction.Removed, outcome.Action);
        Assert.Equal("MAILTO=me\n", machine.Content);
    }

    [Fact]
    public void Install_Crontab_InvalidExpression_FailsLoudly()
    {
        var machine = new CrontabMachine(string.Empty);

        var outcome = CreateCrontab(machine).Install(_repo, new ScheduleRequest("build.yaml", "banana"));

        Assert.Equal(ScheduleAction.Failed, outcome.Action);
        Assert.Contains("build.yaml", outcome.Error!);
        Assert.Contains("banana", outcome.Error!);
        Assert.Empty(machine.Runner.Calls); // never wrote a bad line
    }

    [Fact]
    public void Reconcile_Crontab_AddsDesiredAndRemovesStale()
    {
        var machine = new CrontabMachine("MAILTO=me\n");
        var scheduler = CreateCrontab(machine);
        scheduler.Install(_repo, new ScheduleRequest("old.yaml", "0 5 * * *"));

        var result = scheduler.Reconcile(_repo, [new ScheduleRequest("build.yaml", "0 3 * * *")]);

        Assert.Contains(result.Entries, entry => entry.PipelineFileName == "build.yaml" && entry.Action == ScheduleAction.Created);
        Assert.Contains(result.Entries, entry => entry.PipelineFileName == "old.yaml" && entry.Action == ScheduleAction.Removed);
        Assert.Contains("build.yaml", machine.Content);
        Assert.DoesNotContain("old.yaml", machine.Content);
        Assert.Contains("MAILTO=me", machine.Content);
    }

    // ----- schtasks --------------------------------------------------------------------------------

    [Fact]
    public void Install_Schtasks_WritesExactCreateArgumentsWrappedInCmdForTheRedirect()
    {
        var machine = new SchtasksMachine();
        var scheduler = CreateSchtasks(machine);

        var outcome = scheduler.Install(_shortRepo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        Assert.Equal(ScheduleAction.Created, outcome.Action);

        var repo = Absolute(_shortRepo);
        var repoSlug = _paths.GetRepositorySlug(repo);
        var hash = SchedulerBase.GetEntrySuffix("0 3 * * *");
        var taskName = $"Railway\\{repoSlug}\\build.{hash}";
        var log = Path.Combine(repo, ".railway", "logs", "schedule-build.log");

        // Task Scheduler launches /TR without a shell, so the redirect is only interpreted when the
        // command is wrapped in `cmd /c "…"`; every token is quoted and the cron is forwarded as
        // provenance (`--cron`).
        var command = $"cmd /c \"\"dotnet\" \"/app/Cli.dll\" hooks run schedule --pipeline \"build.yaml\""
            + $" --path \"{repo}\" --cron \"0 3 * * *\" >> \"{log}\" 2>&1\"";

        Assert.Equal(taskName, outcome.EntryName);
        Assert.Contains(taskName, machine.TaskNames);

        var create = machine.Runner.Calls.Single(call => call.Arguments.Contains("/Create"));
        Assert.Equal("schtasks", create.FileName);
        Assert.Equal(
            new[] { "/Create", "/TN", taskName, "/TR", command, "/SC", "DAILY", "/MO", "1", "/ST", "03:00", "/F" },
            create.Arguments);
    }

    [Fact]
    public void Install_Schtasks_CreatesTheLogDirectoryForTheRedirectTarget()
    {
        var machine = new SchtasksMachine();
        var logs = Path.Combine(Absolute(_shortRepo), ".railway", "logs");
        Assert.False(Directory.Exists(logs));

        CreateSchtasks(machine).Install(_shortRepo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        Assert.True(Directory.Exists(logs));
    }

    [Fact]
    public void Install_Schtasks_WeeklyAndMonthlyMappings()
    {
        var weekly = new SchtasksMachine();
        CreateSchtasks(weekly).Install(_shortRepo, new ScheduleRequest("build.yaml", "30 4 * * 1,5"));
        var weeklyCreate = weekly.Runner.Calls.Last(call => call.Arguments.Contains("/Create"));
        Assert.Equal(
            new[] { "/SC", "WEEKLY", "/MO", "1", "/D", "MON,FRI", "/ST", "04:30", "/F" },
            weeklyCreate.Arguments.SkipWhile(argument => argument != "/SC").ToArray());

        var monthly = new SchtasksMachine();
        CreateSchtasks(monthly).Install(_shortRepo, new ScheduleRequest("build.yaml", "0 2 15 * *"));
        var monthlyCreate = monthly.Runner.Calls.Last(call => call.Arguments.Contains("/Create"));
        Assert.Equal(
            new[] { "/SC", "MONTHLY", "/MO", "1", "/D", "15", "/ST", "02:00", "/F" },
            monthlyCreate.Arguments.SkipWhile(argument => argument != "/SC").ToArray());
    }

    [Fact]
    public void Install_Schtasks_MinuteStepMapping()
    {
        var machine = new SchtasksMachine();

        CreateSchtasks(machine).Install(_shortRepo, new ScheduleRequest("build.yaml", "*/15 * * * *"));

        var create = machine.Runner.Calls.Last(call => call.Arguments.Contains("/Create"));
        Assert.Equal(
            new[] { "/SC", "MINUTE", "/MO", "15", "/F" },
            create.Arguments.SkipWhile(argument => argument != "/SC").ToArray());
    }

    [Fact]
    public void Install_Schtasks_UnmappableExpression_FailsLoudlyWithSuggestion()
    {
        var machine = new SchtasksMachine();

        var outcome = CreateSchtasks(machine).Install(_shortRepo, new ScheduleRequest("build.yaml", "0 3 1 * 1"));

        Assert.Equal(ScheduleAction.Failed, outcome.Action);
        Assert.Contains("build.yaml", outcome.Error!);
        Assert.Contains("0 3 1 * 1", outcome.Error!);
        Assert.Contains("schtasks", outcome.Error!);
        Assert.Contains("Nearest equivalent", outcome.Error!);
        Assert.DoesNotContain(machine.Runner.Calls, call => call.Arguments.Contains("/Create"));
    }

    [Fact]
    public void Install_Schtasks_CommandExceedingTaskRunLimit_FailsLoudly()
    {
        // The private /TR limit is 262 characters; a long repository root must fail loudly (not silently
        // truncate) so the user learns the task cannot be installed.
        var machine = new SchtasksMachine();

        var outcome = CreateSchtasks(machine).Install(_repo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        Assert.Equal(ScheduleAction.Failed, outcome.Action);
        Assert.Contains("build.yaml", outcome.Error!);
        Assert.Contains("0 3 * * *", outcome.Error!);
        Assert.Contains("262", outcome.Error!);
        Assert.DoesNotContain(machine.Runner.Calls, call => call.Arguments.Contains("/Create"));
    }

    [Fact]
    public void Inspect_Schtasks_UnmappableExpression_ReportsUnsupported()
    {
        var entry = CreateSchtasks(new SchtasksMachine()).Inspect(_shortRepo, new ScheduleRequest("build.yaml", "0 3 1 * 1"));

        Assert.Equal(ScheduleState.Unsupported, entry.State);
        Assert.Contains("build.yaml", entry.Error!);
        Assert.Contains("0 3 1 * 1", entry.Error!);
    }

    [Fact]
    public void Install_Schtasks_IsIdempotent_SecondRunReportsUnchanged()
    {
        var machine = new SchtasksMachine();
        var scheduler = CreateSchtasks(machine);

        var first = scheduler.Install(_shortRepo, new ScheduleRequest("build.yaml", "0 3 * * *"));
        Assert.Equal(ScheduleAction.Created, first.Action);
        var createsAfterFirst = CountCreates(machine);

        // Ownership is name based: the task now exists at exactly our name, so a second install is a no-op.
        var second = scheduler.Install(_shortRepo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        Assert.Equal(ScheduleAction.Unchanged, second.Action);
        Assert.Equal(createsAfterFirst, CountCreates(machine));
    }

    [Fact]
    public void List_Schtasks_ReportsOwnedEntriesByPipelineSlugAndEntryHash()
    {
        var machine = new SchtasksMachine();
        var scheduler = CreateSchtasks(machine);
        scheduler.Install(_shortRepo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        var entry = Assert.Single(scheduler.List(_shortRepo));

        // Only the name (shape) is recoverable: the pipeline *slug* stands in for the file name and the
        // entry hash stands in for the cron.
        Assert.Equal("build", entry.PipelineFileName);
        Assert.Equal(SchedulerBase.GetCronHash("0 3 * * *"), entry.Cron);
    }

    [Fact]
    public void List_Schtasks_IgnoresForeignTasksInTheOwnedFolder()
    {
        var machine = new SchtasksMachine();
        var scheduler = CreateSchtasks(machine);
        scheduler.Install(_shortRepo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        // A task under our folder whose leaf does not have the <slug>.<8 hex> shape is foreign.
        machine.Seed($"Railway\\{_paths.GetRepositorySlug(Absolute(_shortRepo))}\\notes.txt");

        var entry = Assert.Single(scheduler.List(_shortRepo));
        Assert.Equal("build", entry.PipelineFileName);
    }

    [Fact]
    public void Inspect_Schtasks_ReportsInstalledDriftedAndMissing()
    {
        var scheduler = CreateSchtasks(new SchtasksMachine());
        scheduler.Install(_shortRepo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        Assert.Equal(ScheduleState.Installed, scheduler.Inspect(_shortRepo, new ScheduleRequest("build.yaml", "0 3 * * *")).State);
        Assert.Equal(ScheduleState.Drifted, scheduler.Inspect(_shortRepo, new ScheduleRequest("build.yaml", "0 4 * * *")).State);
        Assert.Equal(ScheduleState.Missing, scheduler.Inspect(_shortRepo, new ScheduleRequest("other.yaml", "0 3 * * *")).State);
    }

    [Fact]
    public void Remove_Schtasks_DeletesOnlyTheOwnedTask()
    {
        var machine = new SchtasksMachine();
        var scheduler = CreateSchtasks(machine);
        scheduler.Install(_shortRepo, new ScheduleRequest("build.yaml", "0 3 * * *"));
        var foreign = $"Railway\\{_paths.GetRepositorySlug(Absolute(_shortRepo))}\\foreign task";
        machine.Seed(foreign);

        var outcome = scheduler.Remove(_shortRepo, "build.yaml", "0 3 * * *");

        Assert.Equal(ScheduleAction.Removed, outcome.Action);
        Assert.Equal(new[] { foreign }, machine.TaskNames.ToArray());
    }

    [Fact]
    public void Reconcile_Schtasks_AddsDesiredAndRemovesStaleOwnedTasks()
    {
        var machine = new SchtasksMachine();
        var scheduler = CreateSchtasks(machine);
        scheduler.Install(_shortRepo, new ScheduleRequest("old.yaml", "0 5 * * *"));

        var result = scheduler.Reconcile(_shortRepo, [new ScheduleRequest("build.yaml", "0 3 * * *")]);

        Assert.Contains(result.Entries, entry => entry.PipelineFileName == "build.yaml" && entry.Action == ScheduleAction.Created);
        Assert.Contains(result.Entries, entry => entry.PipelineFileName == "old" && entry.Action == ScheduleAction.Removed);

        var single = Assert.Single(scheduler.List(_shortRepo));
        Assert.Equal("build", single.PipelineFileName);
    }

    [Fact]
    public void Reconcile_Schtasks_LeavesForeignTasksUntouched()
    {
        var machine = new SchtasksMachine();
        var scheduler = CreateSchtasks(machine);
        var foreign = $"Railway\\{_paths.GetRepositorySlug(Absolute(_shortRepo))}\\foreign task";
        machine.Seed(foreign);

        var result = scheduler.Reconcile(_shortRepo, []);

        Assert.Empty(result.Entries);
        Assert.Contains(foreign, machine.TaskNames);
    }

    // ----- selector --------------------------------------------------------------------------------

    [Theory]
    [InlineData("launchd", "launchd")]
    [InlineData("crontab", "crontab")]
    [InlineData("schtasks", "schtasks")]
    [InlineData("fake", "fake")]
    [InlineData("  LAUNCHD  ", "launchd")]
    public void Selector_EnvironmentOverride_SelectsRequestedScheduler(string value, string expectedName)
    {
        var selector = new SchedulerSelector(
            _paths,
            new RecordingSystemCommandRunner(),
            new FakeLauncher(),
            name => name == SchedulerSelector.EnvironmentVariable ? value : null);

        Assert.Equal(expectedName, selector.Select().Name);
    }

    [Fact]
    public void Selector_UnknownOverride_ThrowsNamingTheValue()
    {
        var selector = new SchedulerSelector(
            _paths,
            new RecordingSystemCommandRunner(),
            new FakeLauncher(),
            name => name == SchedulerSelector.EnvironmentVariable ? "cron-job" : null);

        var exception = Assert.Throws<UnknownSchedulerException>(() => selector.Select());
        Assert.Equal("cron-job", exception.Value);
        Assert.Contains("cron-job", exception.Message);
    }

    [Fact]
    public void Selector_NoOverride_ReturnsPlatformDefault()
    {
        var selector = new SchedulerSelector(_paths, new RecordingSystemCommandRunner(), new FakeLauncher(), _ => null);

        var expected = OperatingSystem.IsWindows() ? "schtasks" : OperatingSystem.IsMacOS() ? "launchd" : "crontab";
        Assert.Equal(expected, selector.Select().Name);
    }

    // ----- fake scheduler --------------------------------------------------------------------------

    [Fact]
    public void FakeScheduler_InstallPersistsStoreAndInspectsInstalledThenDrifted()
    {
        var scheduler = new FakeScheduler(_paths, _fakeRoot, _ => null);

        var outcome = scheduler.Install(_repo, new ScheduleRequest("build.yaml", "0 3 * * *"));

        Assert.Equal(ScheduleAction.Created, outcome.Action);
        var storePath = Path.Combine(_fakeRoot, _paths.GetRepositorySlug(Absolute(_repo)) + ".json");
        Assert.True(File.Exists(storePath));
        Assert.Contains("build.yaml", File.ReadAllText(storePath));

        Assert.Equal(ScheduleState.Installed, scheduler.Inspect(_repo, new ScheduleRequest("build.yaml", "0 3 * * *")).State);
        Assert.Equal(ScheduleState.Drifted, scheduler.Inspect(_repo, new ScheduleRequest("build.yaml", "0 5 * * *")).State);
        Assert.Equal(ScheduleState.Missing, scheduler.Inspect(_repo, new ScheduleRequest("other.yaml", "0 3 * * *")).State);
    }

    // ----- helpers ---------------------------------------------------------------------------------

    private LaunchdScheduler CreateLaunchd(ISystemCommandRunner runner) =>
        new(_paths, runner, rootOverride: _launchdRoot, environment: _ => null);

    private CrontabScheduler CreateCrontab(CrontabMachine machine) =>
        new(_paths, machine.Runner);

    private SchtasksScheduler CreateSchtasks(SchtasksMachine machine) =>
        new(_paths, machine.Runner, new FakeLauncher());

    private static RecordingSystemCommandRunner LaunchdRunner(string uid = "501") =>
        new()
        {
            Handler = (fileName, _, _) => fileName == "id" && uid.Length > 0
                ? new SystemCommandResult(0, uid + "\n", string.Empty)
                : new SystemCommandResult(0, string.Empty, string.Empty),
        };

    private static int CountCreates(SchtasksMachine machine) =>
        machine.Runner.Calls.Count(call => call.Arguments.Contains("/Create"));

    private static string Absolute(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static (string FileName, string Arguments) Describe(RecordedCall call) =>
        (call.FileName, "[" + string.Join(' ', call.Arguments) + "]");

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

    private static string SingleEntry(int minute, int hour) =>
        "    <dict>\n"
        + $"      <key>Minute</key>\n      <integer>{minute}</integer>\n"
        + $"      <key>Hour</key>\n      <integer>{hour}</integer>\n"
        + "    </dict>\n";

    /// <summary>Builds byte-for-byte the plist <see cref="LaunchdScheduler"/> is expected to write.</summary>
    private static string ExpectedPlist(string label, string wrapper, string log, string pipelineFileName, string cron, string calendarEntries) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
        + "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"
        + "<plist version=\"1.0\">\n"
        + "<dict>\n"
        + "  <key>Label</key>\n"
        + $"  <string>{label}</string>\n"
        + "  <key>ProgramArguments</key>\n"
        + "  <array>\n"
        + "    <string>/bin/sh</string>\n"
        + $"    <string>{wrapper}</string>\n"
        + $"    <string>{cron}</string>\n"
        + "  </array>\n"
        + "  <key>RunAtLoad</key>\n"
        + "  <false/>\n"
        + "  <key>StartCalendarInterval</key>\n"
        + "  <array>\n"
        + calendarEntries
        + "  </array>\n"
        + "  <key>StandardOutPath</key>\n"
        + $"  <string>{log}</string>\n"
        + "  <key>StandardErrorPath</key>\n"
        + $"  <string>{log}</string>\n"
        + "  <key>RailwayPipeline</key>\n"
        + $"  <string>{pipelineFileName}</string>\n"
        + "  <key>RailwayCron</key>\n"
        + $"  <string>{cron}</string>\n"
        + "</dict>\n"
        + "</plist>\n";

    /// <summary>A fake <c>crontab</c> machine whose content is mutated by writes, capturing stdin.</summary>
    private sealed class CrontabMachine
    {
        public CrontabMachine(string content)
        {
            Content = content;
            Runner = new RecordingSystemCommandRunner
            {
                Handler = Handle,
            };
        }

        public string Content { get; set; }

        public RecordingSystemCommandRunner Runner { get; }

        private SystemCommandResult Handle(string fileName, IReadOnlyList<string> arguments, string? stdin)
        {
            Assert.Equal("crontab", fileName);

            if (arguments.Count == 1 && arguments[0] == "-")
            {
                Content = stdin ?? string.Empty;
                return new SystemCommandResult(0, string.Empty, string.Empty);
            }

            return Content.Length == 0
                ? new SystemCommandResult(1, string.Empty, "no crontab for user")
                : new SystemCommandResult(0, Content, string.Empty);
        }
    }

    /// <summary>
    /// A fake <c>schtasks</c> task store backed by a real name-keyed dictionary: it models
    /// create/delete/query exactly as the OS does (querying a missing task exits 1, deleting removes it,
    /// listing a folder prints <c>TaskName:</c> lines) so the name-based ownership logic is exercised
    /// non-circularly. No description is modeled: <c>schtasks /Create</c> cannot write one.
    /// </summary>
    private sealed class SchtasksMachine
    {
        private readonly Dictionary<string, string> _tasks = new(StringComparer.OrdinalIgnoreCase);

        public SchtasksMachine()
        {
            Runner = new RecordingSystemCommandRunner { Handler = Handle };
        }

        public RecordingSystemCommandRunner Runner { get; }

        public IReadOnlyCollection<string> TaskNames => _tasks.Keys;

        public void Seed(string taskName, string command = "cmd /c \"echo seeded\"") => _tasks[taskName] = command;

        private SystemCommandResult Handle(string fileName, IReadOnlyList<string> arguments, string? stdin)
        {
            Assert.Equal("schtasks", fileName);

            var taskName = Value(arguments, "/TN");

            return arguments[0] switch
            {
                "/Create" => Create(taskName, Value(arguments, "/TR")),
                "/Delete" => _tasks.Remove(taskName)
                    ? new SystemCommandResult(0, string.Empty, string.Empty)
                    : new SystemCommandResult(1, string.Empty, "ERROR: the system cannot find the task."),
                "/Query" when arguments.Contains("/FO") => QueryFolder(taskName),
                "/Query" => _tasks.ContainsKey(taskName)
                    ? new SystemCommandResult(0, $"TaskName: \\{taskName}\n", string.Empty)
                    : new SystemCommandResult(1, string.Empty, "ERROR: the system cannot find the task."),
                _ => new SystemCommandResult(1, string.Empty, "unsupported schtasks verb"),
            };
        }

        private SystemCommandResult Create(string taskName, string command)
        {
            _tasks[taskName] = command;
            return new SystemCommandResult(0, $"SUCCESS: the scheduled task \"{taskName}\" was created.", string.Empty);
        }

        private SystemCommandResult QueryFolder(string folder)
        {
            // Real schtasks /Query without /TN lists every task on the machine; the caller filters by
            // folder shape. With /TN it lists the given folder.
            var matching = _tasks.Keys
                .Where(name => folder.Length == 0 || name.StartsWith(folder.TrimStart('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            return matching.Length == 0
                ? new SystemCommandResult(1, string.Empty, "ERROR: the system cannot find the task.")
                : new SystemCommandResult(
                    0,
                    string.Join("\n", matching.Select(name => $"TaskName: \\{name}")) + "\n",
                    string.Empty);
        }

        private static string Value(IReadOnlyList<string> arguments, string key)
        {
            for (var index = 0; index < arguments.Count - 1; index++)
            {
                if (string.Equals(arguments[index], key, StringComparison.Ordinal))
                {
                    return arguments[index + 1];
                }
            }

            return string.Empty;
        }
    }
}

/// <summary>An <see cref="ISystemCommandRunner"/> that records files, arguments and stdin for assertions.</summary>
internal sealed class RecordingSystemCommandRunner : ISystemCommandRunner
{
    public List<RecordedCall> Calls { get; } = [];

    public Func<string, IReadOnlyList<string>, string?, SystemCommandResult>? Handler { get; init; }

    public SystemCommandResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        string? stdin = null,
        int timeoutMs = 30_000)
    {
        Calls.Add(new RecordedCall(fileName, [.. arguments], stdin));
        return Handler?.Invoke(fileName, arguments, stdin) ?? new SystemCommandResult(0, string.Empty, string.Empty);
    }

    public SystemCommandResult RunDetached(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null) =>
        new(0, string.Empty, string.Empty);
}

/// <summary>One recorded <see cref="ISystemCommandRunner"/> call.</summary>
internal sealed record RecordedCall(string FileName, IReadOnlyList<string> Arguments, string? Stdin);
