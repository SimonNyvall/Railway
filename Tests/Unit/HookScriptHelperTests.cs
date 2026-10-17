using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Orchestration.Interfaces;

using Xunit;

namespace Tests.Unit;

public sealed class HookScriptHelperTests
{
    [Theory]
    [InlineData(TriggerEvent.PostCommit, "post-commit", "post-commit")]
    [InlineData(TriggerEvent.PrePush, "push", "pre-push")]
    [InlineData(TriggerEvent.PostMerge, "post-merge", "post-merge")]
    [InlineData(TriggerEvent.PostCheckout, "post-checkout", "post-checkout")]
    public void KeywordAndGitHookName_Mapping(TriggerEvent triggerEvent, string keyword, string gitHookName)
    {
        Assert.Equal(keyword, HookScriptHelper.GetEventKeyword(triggerEvent));
        Assert.Equal(gitHookName, HookScriptHelper.GetGitHookName(triggerEvent));
        Assert.True(HookScriptHelper.IsGitHookEvent(triggerEvent));
    }

    [Theory]
    [InlineData(TriggerEvent.Schedule, "schedule")]
    [InlineData(TriggerEvent.Watch, "watch")]
    public void ScheduleAndWatch_HaveNoGitHookName(TriggerEvent triggerEvent, string keyword)
    {
        Assert.Equal(keyword, HookScriptHelper.GetEventKeyword(triggerEvent));
        Assert.Null(HookScriptHelper.GetGitHookName(triggerEvent));
        Assert.False(HookScriptHelper.IsGitHookEvent(triggerEvent));
    }

    [Fact]
    public void GitEvents_UseHookProvenanceKey_ScheduleAndWatchUseTheirOwn()
    {
        Assert.Equal("hook", HookScriptHelper.GetTriggerKey(TriggerEvent.PostCommit));
        Assert.Equal("hook", HookScriptHelper.GetTriggerKey(TriggerEvent.PrePush));
        Assert.Equal("schedule", HookScriptHelper.GetTriggerKey(TriggerEvent.Schedule));
        Assert.Equal("watch", HookScriptHelper.GetTriggerKey(TriggerEvent.Watch));
    }

    [Fact]
    public void HookMarker_And_ManagedScriptDetection()
    {
        Assert.Equal("# railway-managed hook: post-commit", HookScriptHelper.GetHookMarker(TriggerEvent.PostCommit));

        Assert.True(HookScriptHelper.IsRailwayManagedScript("#!/bin/sh\n# railway-managed hook: push\nexit 0\n"));
        Assert.False(HookScriptHelper.IsRailwayManagedScript("#!/bin/sh\necho hi\n"));
        Assert.False(HookScriptHelper.IsRailwayManagedScript(string.Empty));
    }

    [Fact]
    public void QuoteSh_EscapesShellMetacharacters()
    {
        Assert.Equal("\"/tmp/a b\"", HookScriptHelper.QuoteSh("/tmp/a b"));
        Assert.Equal("\"a\\\"b\"", HookScriptHelper.QuoteSh("a\"b"));
        Assert.Equal("\"$HOME\"", HookScriptHelper.QuoteSh("$HOME").Replace("\\$", "$", StringComparison.Ordinal));
        Assert.Contains("\\$", HookScriptHelper.QuoteSh("$HOME"));
        Assert.Contains("\\`", HookScriptHelper.QuoteSh("`whoami`"));
    }

    [Theory]
    [InlineData("/usr/local/share/dotnet/dotnet", "/Users/dev/Cli.dll", "dotnet", "/Users/dev/Cli.dll")]
    [InlineData("dotnet", "/app/Cli.dll", "dotnet", "/app/Cli.dll")]
    [InlineData("/opt/railway/Cli", "/opt/railway/Cli.dll", "/opt/railway/Cli", null)]
    public void DescribeLauncher_CapturesBothProcessShapes(
        string processPath,
        string assemblyLocation,
        string expectedFileName,
        string? expectedArgument)
    {
        var command = HookScriptHelper.DescribeLauncher(processPath, assemblyLocation);

        Assert.Equal(expectedFileName, command.FileName);

        if (expectedArgument is null)
        {
            Assert.Empty(command.Arguments);
        }
        else
        {
            Assert.Equal([expectedArgument], command.Arguments);
        }
    }

    [Fact]
    public void DescribeLauncher_DotnetHostWithoutAssemblyLocation_FallsBackToProcessPath()
    {
        var command = HookScriptHelper.DescribeLauncher("/usr/bin/dotnet", null);

        Assert.Equal("/usr/bin/dotnet", command.FileName);
        Assert.Empty(command.Arguments);
    }

    [Fact]
    public void LaunchCommand_ToShellCommand_QuotesEveryToken()
    {
        var command = new HookLaunchCommand("dotnet", ["/my path/Cli.dll"]);

        Assert.Equal("\"dotnet\" \"/my path/Cli.dll\"", command.ToShellCommand());
        Assert.Equal("\"dotnet\" \"/my path/Cli.dll\"", command.WithArguments().ToShellCommand());
        Assert.Equal("\"dotnet\" \"/my path/Cli.dll\" \"hooks\"", command.WithArguments("hooks").ToShellCommand());
    }

    [Fact]
    public void BuildWrapperScript_IsPosixShWithRepoRootAndLauncherAndDetach()
    {
        var script = HookScriptHelper.BuildWrapperScript(
            TriggerEvent.PostCommit,
            "/repo root",
            new HookLaunchCommand("dotnet", ["/app/Cli.dll"]),
            detach: true);

        Assert.StartsWith("#!/bin/sh\n", script);
        Assert.Contains("# railway-managed hook: post-commit\n", script);
        Assert.Contains("cd \"/repo root\" || exit 0\n", script);
        Assert.Contains("\"dotnet\" \"/app/Cli.dll\" hooks run post-commit --path \"/repo root\" --detach\n", script);
        Assert.EndsWith("exit 0\n", script);
        Assert.DoesNotContain("\r", script);
        Assert.EndsWith("\n", script);
    }

    [Fact]
    public void BuildWrapperScript_WithoutDetach_PropagatesExitCode()
    {
        var script = HookScriptHelper.BuildWrapperScript(
            TriggerEvent.PostCommit,
            "/repo",
            new HookLaunchCommand("/opt/Cli", []),
            detach: false);

        Assert.Contains("\"/opt/Cli\" hooks run post-commit --path \"/repo\"\n", script);
        Assert.DoesNotContain("--detach", script);
        Assert.EndsWith("exit $?\n", script);
    }

    [Fact]
    public void BuildWrapperScript_PrePush_ParsesStdinRefsAndNeverBlocks()
    {
        var script = HookScriptHelper.BuildWrapperScript(
            TriggerEvent.PrePush,
            "/repo root",
            new HookLaunchCommand("dotnet", ["/app/Cli.dll"]),
            detach: true);

        Assert.StartsWith("#!/bin/sh\n", script);
        Assert.Contains("# railway-managed hook: push\n", script);
        Assert.Contains("cd \"/repo root\" || exit 0\n", script);

        // Remote name is taken from the first git argument; stdin supplies one line per pushed ref.
        Assert.Contains("_railway_remote=\"$1\"\n", script);
        Assert.Contains("while read -r _railway_local_ref _railway_local_sha _railway_remote_ref _railway_remote_sha; do\n", script);

        // Deletions are skipped (git reports '(delete)' and an all-zero local sha).
        Assert.Contains("if [ \"$_railway_local_ref\" = \"(delete)\" ] || _railway_is_zero \"$_railway_local_sha\"; then\n", script);

        // Branch and tag refs spawn a detached run with the full context.
        Assert.Contains("refs/heads/*)\n", script);
        Assert.Contains("refs/tags/*)\n", script);
        Assert.Contains(
            "\"dotnet\" \"/app/Cli.dll\" hooks run push --branch \"${_railway_local_ref#refs/heads/}\" --sha \"$_railway_local_sha\" --remote \"$_railway_remote\" --path \"/repo root\" --detach < /dev/null\n",
            script);
        Assert.Contains(
            "\"dotnet\" \"/app/Cli.dll\" hooks run push --tag \"${_railway_local_ref#refs/tags/}\" --sha \"$_railway_local_sha\" --remote \"$_railway_remote\" --path \"/repo root\" --detach < /dev/null\n",
            script);

        Assert.EndsWith("exit 0\n", script);
        Assert.DoesNotContain("\r", script);
        Assert.DoesNotContain("[[", script);
        Assert.DoesNotContain("bash", script);
    }

    [Fact]
    public void BuildWrapperScript_PostMerge_ForwardsSquashFlagAndNeverBlocks()
    {
        var script = HookScriptHelper.BuildWrapperScript(
            TriggerEvent.PostMerge,
            "/repo",
            new HookLaunchCommand("/opt/Cli", []),
            detach: true);

        Assert.StartsWith("#!/bin/sh\n", script);
        Assert.Contains("# railway-managed hook: post-merge\n", script);
        Assert.Contains("\"/opt/Cli\" hooks run post-merge --is-squash \"$1\" --path \"/repo\" --detach < /dev/null\n", script);
        Assert.EndsWith("exit 0\n", script);
        Assert.DoesNotContain("\r", script);
    }

    [Fact]
    public void BuildWrapperScript_PostCheckout_OnlyFiresForBranchCheckoutsAndNeverBlocks()
    {
        var script = HookScriptHelper.BuildWrapperScript(
            TriggerEvent.PostCheckout,
            "/repo",
            new HookLaunchCommand("/opt/Cli", []),
            detach: true);

        Assert.StartsWith("#!/bin/sh\n", script);
        Assert.Contains("# railway-managed hook: post-checkout\n", script);

        // git passes <prev> <new> <flag>; only flag=1 (branch checkout) proceeds.
        Assert.Contains("[ \"$3\" = \"1\" ] || exit 0\n", script);
        Assert.Contains(
            "\"/opt/Cli\" hooks run post-checkout --prev \"$1\" --new \"$2\" --flag \"$3\" --path \"/repo\" --detach < /dev/null\n",
            script);
        Assert.EndsWith("exit 0\n", script);
        Assert.DoesNotContain("\r", script);
        Assert.DoesNotContain("[[", script);
    }

    [Fact]
    public void BuildGitHookScript_ChainsBackupAndPreservesItsExitCode()
    {
        var script = HookScriptHelper.BuildGitHookScript(
            TriggerEvent.PostMerge,
            "/repo/.railway/hooks/merge.sh",
            "/repo/.git/hooks/post-merge.railway-backup");

        Assert.StartsWith("#!/bin/sh\n", script);
        Assert.Contains("# railway-managed hook: post-merge\n", script);
        Assert.Contains("sh \"/repo/.railway/hooks/merge.sh\" \"$@\"\n", script);
        Assert.Contains("if [ -f \"/repo/.git/hooks/post-merge.railway-backup\" ]; then\n", script);
        Assert.Contains("  sh \"/repo/.git/hooks/post-merge.railway-backup\" \"$@\"\n", script);
        Assert.Contains("  exit $?\n", script);
        Assert.DoesNotContain("\r", script);
    }

    [Fact]
    public void BuildGitHookScript_PrePushWithBackup_BuffersStdinForBothHooks()
    {
        var script = HookScriptHelper.BuildGitHookScript(
            TriggerEvent.PrePush,
            "/repo/.railway/hooks/push.sh",
            "/repo/.git/hooks/pre-push.railway-backup");

        Assert.StartsWith("#!/bin/sh\n", script);
        Assert.Contains("# railway-managed hook: push\n", script);

        // git's ref lines are buffered so the wrapper (which drains stdin) and the chained foreign hook
        // both receive the same input.
        Assert.Contains("cat > \"$_railway_stdin\"\n", script);
        Assert.Contains("sh \"/repo/.railway/hooks/push.sh\" \"$@\" < \"$_railway_stdin\"\n", script);
        Assert.Contains("  sh \"/repo/.git/hooks/pre-push.railway-backup\" \"$@\" < \"$_railway_stdin\"\n", script);

        // The temp file is cleaned up on every path and the backup's exit code is preserved.
        Assert.Contains("  rm -f \"$_railway_stdin\" 2>/dev/null\n", script);
        Assert.Contains("  exit $_railway_brc\n", script);

        // A machine without mktemp degrades to the unbuffered chain (correct exit codes, empty stdin).
        Assert.Contains("if [ -z \"$_railway_stdin\" ]; then\n", script);
        Assert.DoesNotContain("\r", script);
    }

    [Fact]
    public void BuildGitHookScript_WithoutBackup_HasNoChainBlock()
    {
        var script = HookScriptHelper.BuildGitHookScript(TriggerEvent.PostCommit, "/r/w.sh", null);

        Assert.DoesNotContain(".railway-backup", script);
        Assert.EndsWith("exit $_railway_rc\n", script);
    }

    [Fact]
    public void BuildGitHookScript_NonGitEvent_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HookScriptHelper.BuildGitHookScript(TriggerEvent.Schedule, "/r/w.sh", null));

    [Fact]
    public void BuildScheduleWrapperScript_BakesPathAndForwardsTheEntryCronArgument()
    {
        var script = HookScriptHelper.BuildScheduleWrapperScript(
            "build.yaml",
            "/repo root",
            new HookLaunchCommand("dotnet", ["/app/Cli.dll"]),
            "/opt/dotnet:/usr/bin");

        Assert.StartsWith("#!/bin/sh\n", script);
        Assert.Contains("# railway-managed hook: schedule\n", script);
        Assert.Contains("export PATH=\"/opt/dotnet:/usr/bin\"\n", script);
        Assert.Contains("cd \"/repo root\" || exit 0\n", script);

        // The scheduler entry passes its cron as the first shell argument, so the run records provenance.
        Assert.Contains(
            "\"dotnet\" \"/app/Cli.dll\" hooks run schedule --pipeline \"build.yaml\" --path \"/repo root\" --cron \"$1\"\n",
            script);
        Assert.EndsWith("exit $?\n", script);
        Assert.DoesNotContain("\r", script);
    }

    [Fact]
    public void HookEventContext_GetDetail_RecordsCronProvenance()
    {
        Assert.Equal(
            "branch=main, cron=0 3 * * *",
            new HookEventContext(Branch: "main", Cron: "0 3 * * *").GetDetail());
        Assert.Equal("cron=*/5 * * * *", new HookEventContext(Cron: "*/5 * * * *").GetDetail());
    }

    [Fact]
    public void UpsertGitignoreBlock_AppendsOnceAndIsIdempotent()
    {
        var (first, changed) = HookScriptHelper.UpsertGitignoreBlock("bin/\n");
        Assert.True(changed);
        Assert.Contains("bin/\n", first);
        Assert.Contains(HookScriptHelper.GitignoreBlockStart, first);
        Assert.Contains(".railway/hooks/", first);
        Assert.Contains(".railway/logs/", first);

        var (second, changedAgain) = HookScriptHelper.UpsertGitignoreBlock(first);
        Assert.False(changedAgain);
        Assert.Equal(first, second);
    }

    [Fact]
    public void UpsertGitignoreBlock_RefreshesADriftedBlockInPlace()
    {
        var tampered = $"bin/\n{HookScriptHelper.GitignoreBlockStart}\nstale/\n{HookScriptHelper.GitignoreBlockEnd}\nobj/\n";

        var (content, changed) = HookScriptHelper.UpsertGitignoreBlock(tampered);

        Assert.True(changed);
        Assert.DoesNotContain("stale/", content);
        Assert.Contains("bin/\n", content);
        Assert.Contains("obj/\n", content);
        Assert.Contains(".railway/hooks/", content);
    }

    [Fact]
    public void RemoveGitignoreBlock_RemovesExactlyTheBlock()
    {
        const string original = "bin/\nobj/\n";
        var withBlock = HookScriptHelper.UpsertGitignoreBlock(original).Content;

        var (content, removed) = HookScriptHelper.RemoveGitignoreBlock(withBlock);

        Assert.True(removed);
        Assert.Equal(original, content);

        var (unchanged, removedAgain) = HookScriptHelper.RemoveGitignoreBlock(content);
        Assert.False(removedAgain);
        Assert.Equal(content, unchanged);
    }

    [Fact]
    public void RemoveGitignoreBlock_HandlesContentThatIsOnlyTheBlock()
    {
        var withBlock = HookScriptHelper.UpsertGitignoreBlock(null).Content;

        var (content, removed) = HookScriptHelper.RemoveGitignoreBlock(withBlock);

        Assert.True(removed);
        Assert.Equal(string.Empty, content);
    }

    [Fact]
    public void SchedulerMarkers_AreStable()
    {
        Assert.Equal("# railway: myrepo:build begin", HookScriptHelper.GetCrontabBlockStart("myrepo", "build"));
        Assert.Equal("# railway: myrepo:build end", HookScriptHelper.GetCrontabBlockEnd("myrepo", "build"));
        Assert.True(HookScriptHelper.IsCrontabMarkerLine("  # railway: myrepo:build begin"));
        Assert.False(HookScriptHelper.IsCrontabMarkerLine("0 3 * * * railway hooks run schedule"));

        Assert.Equal("com.railway.myrepo.build", HookScriptHelper.GetLaunchdLabel("myrepo", "build"));
        Assert.Equal(@"Railway\myrepo\build", HookScriptHelper.GetSchtasksTaskName("myrepo", "build"));
    }
}
