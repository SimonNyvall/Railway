using System.Reflection;
using System.Text;

using Railway.Engine.Domain;

namespace Railway.Engine.Helpers;

/// <summary>
/// Templates and marker conventions for hook artifacts: POSIX <c>sh</c> wrapper scripts, git hook
/// files with chain/backup logic, the <c>.gitignore</c> marker block, and scheduler-entry markers
/// (see <see cref="Railway.Engine.Domain.TriggerEvent.Schedule"/>). All methods are pure string/number work; nothing here touches the file system.
/// </summary>
/// <remarks>
/// Scripts are written for <c>sh</c> (Git for Windows executes hooks through <c>sh</c>), use LF line
/// endings only, and avoid bashisms. Paths embedded in scripts are absolute and shell-quoted.
/// </remarks>
public static class HookScriptHelper
{
    /// <summary>Marker comment prefix identifying railway-managed git hook files and wrappers.</summary>
    public const string HookMarkerPrefix = "# railway-managed hook:";

    /// <summary>Start marker of the single marker-delimited <c>.gitignore</c> block.</summary>
    public const string GitignoreBlockStart = "# >>> railway managed hooks >>>";

    /// <summary>End marker of the single marker-delimited <c>.gitignore</c> block.</summary>
    public const string GitignoreBlockEnd = "# <<< railway managed hooks <<<";

    /// <summary>Entries covered by the <c>.gitignore</c> block (machine-specific launcher paths + logs).</summary>
    public static readonly IReadOnlyList<string> GitignoreBlockEntries = [".railway/hooks/", ".railway/logs/"];

    /// <summary>Prefix of cron block markers (<c># railway: &lt;repoSlug&gt;:&lt;pipelineSlug&gt; …</c>).</summary>
    public const string CrontabMarkerPrefix = "# railway:";

    /// <summary>Suffix appended to a git hook file when a foreign hook is chained.</summary>
    public const string BackupSuffix = ".railway-backup";

    /// <summary>Launcher executable name that means "running under the dotnet host".</summary>
    private const string DotnetHostName = "dotnet";

    private static readonly Dictionary<TriggerEvent, string> KeywordsByEvent = new()
    {
        [TriggerEvent.PostCommit] = "post-commit",
        [TriggerEvent.PrePush] = "push",
        [TriggerEvent.PostMerge] = "post-merge",
        [TriggerEvent.PostCheckout] = "post-checkout",
        [TriggerEvent.Schedule] = "schedule",
        [TriggerEvent.Watch] = "watch",
    };

    private static readonly Dictionary<TriggerEvent, string> GitHookNamesByEvent = new()
    {
        [TriggerEvent.PostCommit] = "post-commit",
        [TriggerEvent.PrePush] = "pre-push",
        [TriggerEvent.PostMerge] = "post-merge",
        [TriggerEvent.PostCheckout] = "post-checkout",
    };

    /// <summary>The YAML keyword for an event (<c>post-commit</c>, <c>push</c>, <c>schedule</c>, …).</summary>
    /// <param name="triggerEvent">The trigger event.</param>
    /// <exception cref="ArgumentOutOfRangeException">When the event is unknown.</exception>
    public static string GetEventKeyword(TriggerEvent triggerEvent) =>
        KeywordsByEvent.TryGetValue(triggerEvent, out var keyword)
            ? keyword
            : throw new ArgumentOutOfRangeException(nameof(triggerEvent), triggerEvent, "Unknown trigger event.");

    /// <summary>
    /// The git hook file name an event installs, or null for non-git events
    /// (<see cref="TriggerEvent.Schedule"/>, <see cref="TriggerEvent.Watch"/>).
    /// </summary>
    /// <param name="triggerEvent">The trigger event.</param>
    public static string? GetGitHookName(TriggerEvent triggerEvent) =>
        GitHookNamesByEvent.TryGetValue(triggerEvent, out var name) ? name : null;

    /// <summary>True when the event is materialized as a git hook (as opposed to a scheduler or watcher entry).</summary>
    /// <param name="triggerEvent">The trigger event.</param>
    public static bool IsGitHookEvent(TriggerEvent triggerEvent) => GetGitHookName(triggerEvent) is not null;

    /// <summary>
    /// The provenance key recorded on hook-triggered runs: <c>hook</c> for git events,
    /// <c>schedule</c>/<c>watch</c> for their events.
    /// </summary>
    /// <param name="triggerEvent">The trigger event.</param>
    public static string GetTriggerKey(TriggerEvent triggerEvent) => triggerEvent switch
    {
        TriggerEvent.Schedule => "schedule",
        TriggerEvent.Watch => "watch",
        _ => "hook",
    };

    /// <summary>The marker comment identifying our ownership of a generated hook file or wrapper.</summary>
    /// <param name="triggerEvent">The trigger event the artifact belongs to.</param>
    public static string GetHookMarker(TriggerEvent triggerEvent) =>
        $"{HookMarkerPrefix} {GetEventKeyword(triggerEvent)}";

    /// <summary>True when <paramref name="content"/> carries a railway hook marker.</summary>
    /// <param name="content">File content to inspect.</param>
    public static bool IsRailwayManagedScript(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        foreach (var line in content.Split('\n'))
        {
            if (line.TrimEnd('\r').StartsWith(HookMarkerPrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Quotes a value for POSIX <c>sh</c> double-quoted context.</summary>
    /// <param name="value">Raw value (path, argument, …).</param>
    public static string QuoteSh(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var ch in value)
        {
            if (ch is '"' or '\\' or '`' or '$')
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>
    /// Builds the repo wrapper script that git hooks invoke. It changes into the repository root and
    /// calls the railway CLI headlessly. The three non-blocking git events with argument/stdin contracts
    /// are handled by dedicated templates:
    /// <list type="bullet">
    /// <item><c>pre-push</c>: reads git's ref lines from stdin and spawns one detached run per pushed ref.</item>
    /// <item><c>post-merge</c>: forwards git's squash flag.</item>
    /// <item><c>post-checkout</c>: forwards previous/new HEAD and fires only for branch checkouts.</item>
    /// </list>
    /// All three always <c>exit 0</c> so a failing pipeline can never block the git operation. The
    /// remaining events (e.g. <c>post-commit</c>) use the simple template honoring <paramref name="detach"/>.
    /// </summary>
    /// <param name="triggerEvent">The event the wrapper handles.</param>
    /// <param name="repositoryRoot">Absolute repository root the wrapper changes into.</param>
    /// <param name="launcher">How to invoke the railway CLI (see <see cref="IHookLauncher"/>).</param>
    /// <param name="detach">
    /// True for non-blocking events: the CLI spawns a detached child and the wrapper always exits 0
    /// so git is never blocked by a failing pipeline. Only the simple template (post-commit) reads it.
    /// </param>
    public static string BuildWrapperScript(
        TriggerEvent triggerEvent,
        string repositoryRoot,
        HookLaunchCommand launcher,
        bool detach)
    {
        ArgumentNullException.ThrowIfNull(launcher);

        return triggerEvent switch
        {
            TriggerEvent.PrePush => BuildPrePushWrapperScript(repositoryRoot, launcher),
            TriggerEvent.PostMerge => BuildPostMergeWrapperScript(repositoryRoot, launcher),
            TriggerEvent.PostCheckout => BuildPostCheckoutWrapperScript(repositoryRoot, launcher),
            _ => BuildSimpleWrapperScript(triggerEvent, repositoryRoot, launcher, detach),
        };
    }

    /// <summary>Builds the simple wrapper (no argument/stdin contract) used by <c>post-commit</c>.</summary>
    private static string BuildSimpleWrapperScript(
        TriggerEvent triggerEvent,
        string repositoryRoot,
        HookLaunchCommand launcher,
        bool detach)
    {
        var keyword = GetEventKeyword(triggerEvent);
        var quotedRoot = QuoteSh(repositoryRoot);
        var builder = BeginWrapper(triggerEvent, repositoryRoot);

        builder.Append(launcher.ToShellCommand());
        builder.Append($" hooks run {keyword} --path {quotedRoot}");

        if (detach)
        {
            builder.Append(" --detach");
        }

        builder.Append('\n');

        // `hooks run` exits 1 when a run fails; detached (non-blocking) wrappers must always exit 0.
        builder.Append(detach ? "exit 0\n" : "exit $?\n");

        return builder.ToString();
    }

    /// <summary>
    /// Builds the <c>pre-push</c> wrapper: it consumes every stdin ref line first (so git never sees a
    /// broken pipe), skips deletions, and spawns one detached <c>hooks run push</c> per pushed branch
    /// (<c>--branch</c>) or tag (<c>--tag</c>). Always exits 0 — git's pre-push contract is never aborted.
    /// </summary>
    private static string BuildPrePushWrapperScript(string repositoryRoot, HookLaunchCommand launcher)
    {
        var command = launcher.ToShellCommand();
        var root = QuoteSh(repositoryRoot);
        var builder = BeginWrapper(TriggerEvent.PrePush, repositoryRoot);

        builder.Append("# git feeds '<local-ref> <local-sha> <remote-ref> <remote-sha>' lines on stdin;\n");
        builder.Append("# consume them all before returning (no background reader) and never abort the push.\n");
        builder.Append("_railway_remote=\"$1\"\n");
        builder.Append("_railway_is_zero() {\n");
        builder.Append("  case \"$1\" in\n");
        builder.Append("    \"\"|*[!0]*) return 1 ;;\n");
        builder.Append("    *) return 0 ;;\n");
        builder.Append("  esac\n");
        builder.Append("}\n");
        builder.Append("while read -r _railway_local_ref _railway_local_sha _railway_remote_ref _railway_remote_sha; do\n");
        builder.Append("  [ -n \"$_railway_local_ref\" ] || continue\n");
        builder.Append("  # Skip deletions: git reports '(delete)' and an all-zero local sha.\n");
        builder.Append("  if [ \"$_railway_local_ref\" = \"(delete)\" ] || _railway_is_zero \"$_railway_local_sha\"; then\n");
        builder.Append("    continue\n");
        builder.Append("  fi\n");
        builder.Append("  case \"$_railway_local_ref\" in\n");
        builder.Append("    refs/heads/*)\n");
        builder.Append($"      {command} hooks run push --branch \"${{_railway_local_ref#refs/heads/}}\" --sha \"$_railway_local_sha\" --remote \"$_railway_remote\" --path {root} --detach < /dev/null\n");
        builder.Append("      ;;\n");
        builder.Append("    refs/tags/*)\n");
        builder.Append($"      {command} hooks run push --tag \"${{_railway_local_ref#refs/tags/}}\" --sha \"$_railway_local_sha\" --remote \"$_railway_remote\" --path {root} --detach < /dev/null\n");
        builder.Append("      ;;\n");
        builder.Append("  esac\n");
        builder.Append("done\n");
        builder.Append("exit 0\n");

        return builder.ToString();
    }

    /// <summary>
    /// Builds the <c>post-merge</c> wrapper: forwards git's squash flag (<c>$1</c>, <c>0</c>/<c>1</c>) and
    /// spawns a detached run. Always exits 0.
    /// </summary>
    private static string BuildPostMergeWrapperScript(string repositoryRoot, HookLaunchCommand launcher)
    {
        var command = launcher.ToShellCommand();
        var root = QuoteSh(repositoryRoot);
        var builder = BeginWrapper(TriggerEvent.PostMerge, repositoryRoot);

        builder.Append("# git runs post-merge with the squash flag as its first argument ('1' for a squash\n");
        builder.Append("# merge); the run is detached so a failing pipeline never blocks the merge.\n");
        builder.Append("if [ -n \"$1\" ]; then\n");
        builder.Append($"  {command} hooks run post-merge --is-squash \"$1\" --path {root} --detach < /dev/null\n");
        builder.Append("fi\n");
        builder.Append("exit 0\n");

        return builder.ToString();
    }

    /// <summary>
    /// Builds the <c>post-checkout</c> wrapper: forwards git's previous/new HEAD and flag, fires only for
    /// branch checkouts (<c>flag=1</c>), and spawns a detached run. Clones (previous HEAD all zeros) are
    /// excluded per pipeline by the CLI unless the pipeline declared <c>post-checkout: { clone: true }</c>.
    /// Always exits 0.
    /// </summary>
    private static string BuildPostCheckoutWrapperScript(string repositoryRoot, HookLaunchCommand launcher)
    {
        var command = launcher.ToShellCommand();
        var root = QuoteSh(repositoryRoot);
        var builder = BeginWrapper(TriggerEvent.PostCheckout, repositoryRoot);

        builder.Append("# git runs post-checkout with: <prev-head> <new-head> <flag>; flag '1' is a branch\n");
        builder.Append("# checkout, '0' is a file-level checkout (which must not fire). Clone checkouts are\n");
        builder.Append("# filtered per pipeline by the CLI unless it declared 'post-checkout: { clone: true }'.\n");
        builder.Append("[ \"$3\" = \"1\" ] || exit 0\n");
        builder.Append("if [ -n \"$1\" ]; then\n");
        builder.Append($"  {command} hooks run post-checkout --prev \"$1\" --new \"$2\" --flag \"$3\" --path {root} --detach < /dev/null\n");
        builder.Append("fi\n");
        builder.Append("exit 0\n");

        return builder.ToString();
    }

    /// <summary>Begins a wrapper script: shebang, ownership marker, regeneration hint and the <c>cd</c> guard.</summary>
    private static StringBuilder BeginWrapper(TriggerEvent triggerEvent, string repositoryRoot)
    {
        var builder = new StringBuilder();
        builder.Append("#!/bin/sh\n");
        builder.Append(GetHookMarker(triggerEvent)).Append('\n');
        builder.Append("# Generated by Railway. Do not edit; run 'railway hooks sync' to refresh.\n");
        builder.Append($"cd {QuoteSh(repositoryRoot)} || exit 0\n");
        return builder;
    }

    /// <summary>
    /// Builds the git hook file installed into the git hooks directory. It invokes the wrapper and,
    /// when the original (foreign) hook was moved to <c>&lt;hook&gt;.railway-backup</c>, chain-calls it and
    /// preserves its exit code.
    /// </summary>
    /// <param name="triggerEvent">The git event this hook file handles.</param>
    /// <param name="wrapperPath">Absolute path of the generated wrapper script.</param>
    /// <param name="backupPath">Absolute path of the chained foreign hook, or null when there is none.</param>
    /// <remarks>
    /// <c>pre-push</c> is special: the wrapper drains git's ref lines from stdin, so a chained foreign
    /// <c>pre-push</c> would otherwise receive empty stdin (breaking hooks that read it). When there is
    /// a backup, the wrapper's stdin is buffered to a temp file first and both hooks are fed from it.
    /// When no temp file can be created the script degrades to the unbuffered chain (correct exit codes,
    /// empty stdin — the pre-fix behavior).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">When the event is not a git hook event.</exception>
    public static string BuildGitHookScript(TriggerEvent triggerEvent, string wrapperPath, string? backupPath)
    {
        var hookName = GetGitHookName(triggerEvent)
            ?? throw new ArgumentOutOfRangeException(nameof(triggerEvent), triggerEvent, "Event is not a git hook event.");

        var builder = new StringBuilder();
        builder.Append("#!/bin/sh\n");
        builder.Append(GetHookMarker(triggerEvent)).Append('\n');
        builder.Append($"# Managed by Railway ('railway hooks uninstall' restores the original {hookName}).\n");

        if (triggerEvent == TriggerEvent.PrePush && backupPath is not null)
        {
            // Buffer git's ref lines because the wrapper consumes stdin; both hooks then see the same input.
            builder.Append("_railway_stdin=\"$(mktemp 2>/dev/null || mktemp -t railway-prepush 2>/dev/null)\"\n");
            builder.Append("if [ -z \"$_railway_stdin\" ]; then\n");
            builder.Append($"  sh {QuoteSh(wrapperPath)} \"$@\"\n");
            builder.Append("  _railway_rc=$?\n");
            builder.Append($"  if [ -f {QuoteSh(backupPath)} ]; then sh {QuoteSh(backupPath)} \"$@\"; exit $?; fi\n");
            builder.Append("  exit $_railway_rc\n");
            builder.Append("fi\n");
            builder.Append("cat > \"$_railway_stdin\"\n");
            builder.Append($"sh {QuoteSh(wrapperPath)} \"$@\" < \"$_railway_stdin\"\n");
            builder.Append("_railway_rc=$?\n");
            builder.Append($"if [ -f {QuoteSh(backupPath)} ]; then\n");
            builder.Append($"  sh {QuoteSh(backupPath)} \"$@\" < \"$_railway_stdin\"\n");
            builder.Append("  _railway_brc=$?\n");
            builder.Append("  rm -f \"$_railway_stdin\" 2>/dev/null\n");
            builder.Append("  exit $_railway_brc\n");
            builder.Append("fi\n");
            builder.Append("rm -f \"$_railway_stdin\" 2>/dev/null\n");
            builder.Append("exit $_railway_rc\n");
            return builder.ToString();
        }

        builder.Append($"sh {QuoteSh(wrapperPath)} \"$@\"\n");
        builder.Append("_railway_rc=$?\n");

        if (backupPath is not null)
        {
            builder.Append($"if [ -f {QuoteSh(backupPath)} ]; then\n");
            builder.Append($"  sh {QuoteSh(backupPath)} \"$@\"\n");
            builder.Append("  exit $?\n");
            builder.Append("fi\n");
        }

        builder.Append("exit $_railway_rc\n");
        return builder.ToString();
    }

    /// <summary>
    /// Name of the generated per-pipeline schedule wrapper (<c>schedule-&lt;pipelineSlug&gt;.sh</c>).
    /// </summary>
    /// <param name="pipelineFileName">Pipeline file name, e.g. <c>build.yaml</c>.</param>
    public static string GetScheduleWrapperScriptName(string pipelineFileName) =>
        $"schedule-{HookLock.Slugify(Path.GetFileNameWithoutExtension(pipelineFileName))}.sh";

    /// <summary>
    /// Builds the per-pipeline wrapper a scheduler entry invokes for a <c>schedule</c> trigger. Unlike the
    /// git wrappers it targets a single pipeline (<c>--pipeline</c>), runs the pipeline to completion (no
    /// <c>--detach</c>; the scheduler owns process lifetime), bakes an explicit <c>PATH</c> so the
    /// minimal environment cron/launchd provide can still resolve the launcher, and forwards the schedule
    /// entry's cron expression (its first argument) as <c>--cron "$1"</c> so the run records provenance.
    /// </summary>
    /// <param name="pipelineFileName">Pipeline file name the wrapper restricts the run to.</param>
    /// <param name="repositoryRoot">Absolute repository root the wrapper changes into.</param>
    /// <param name="launcher">How to invoke the railway CLI (see <see cref="IHookLauncher"/>).</param>
    /// <param name="safePath">
    /// Value for the wrapper's <c>PATH</c>, or null to leave the scheduler's environment untouched.
    /// </param>
    public static string BuildScheduleWrapperScript(
        string pipelineFileName,
        string repositoryRoot,
        HookLaunchCommand launcher,
        string? safePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineFileName);
        ArgumentNullException.ThrowIfNull(launcher);

        var quotedRoot = QuoteSh(repositoryRoot);
        var builder = new StringBuilder();

        builder.Append("#!/bin/sh\n");
        builder.Append(GetHookMarker(TriggerEvent.Schedule)).Append('\n');
        builder.Append("# Generated by Railway. Do not edit; run 'railway hooks sync' to refresh.\n");
        builder.Append("# The schedule entry passes its cron expression as the first argument ($1).\n");

        if (!string.IsNullOrEmpty(safePath))
        {
            builder.Append($"export PATH={QuoteSh(safePath)}\n");
        }

        builder.Append($"cd {quotedRoot} || exit 0\n");
        builder.Append(launcher.ToShellCommand());
        builder.Append($" hooks run {GetEventKeyword(TriggerEvent.Schedule)} --pipeline {QuoteSh(pipelineFileName)} --path {quotedRoot} --cron \"$1\"\n");
        builder.Append("exit $?\n");

        return builder.ToString();
    }

    /// <summary>
    /// Builds the <c>PATH</c> value baked into a schedule wrapper: the launcher's directory (when the
    /// launcher is an absolute path) followed by the conventional POSIX binary directories, so a
    /// <c>dotnet</c>-hosted CLI resolves even under cron/launchd's minimal environment.
    /// </summary>
    /// <param name="launcher">How to invoke the railway CLI.</param>
    public static string BuildScheduleSafePath(HookLaunchCommand launcher)
    {
        ArgumentNullException.ThrowIfNull(launcher);

        var directories = new List<string>();
        var launcherDirectory = Path.GetDirectoryName(launcher.FileName);

        if (Path.IsPathRooted(launcher.FileName) && !string.IsNullOrEmpty(launcherDirectory))
        {
            directories.Add(launcherDirectory);
        }

        directories.AddRange(["/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"]);
        return string.Join(':', directories);
    }

    /// <summary>
    /// Adds (or refreshes) the single marker-delimited <c>.gitignore</c> block covering
    /// <see cref="GitignoreBlockEntries"/>. Idempotent: an already-canonical block is left untouched.
    /// </summary>
    /// <param name="content">Current <c>.gitignore</c> content, or null when the file does not exist.</param>
    /// <returns>The new content and whether anything changed.</returns>
    public static (string Content, bool Changed) UpsertGitignoreBlock(string? content)
    {
        var canonical = BuildGitignoreBlock();
        var current = content ?? string.Empty;

        var start = current.IndexOf(GitignoreBlockStart, StringComparison.Ordinal);

        if (start >= 0)
        {
            var end = current.IndexOf(GitignoreBlockEnd, start, StringComparison.Ordinal);
            var existingEnd = end >= 0 ? end + GitignoreBlockEnd.Length : current.Length;

            // The canonical block ends with a newline; also swallow the newline terminating the existing
            // block so the replacement is exact (otherwise an extra blank line accumulates each upsert).
            if (existingEnd < current.Length && current[existingEnd] == '\n')
            {
                existingEnd++;
            }

            var merged = string.Concat(current[..start], canonical, current[existingEnd..]);
            return (merged, !string.Equals(current, merged, StringComparison.Ordinal));
        }

        if (current.Length == 0)
        {
            return (canonical, true);
        }

        var separator = current.EndsWith('\n') ? string.Empty : "\n";
        return (string.Concat(current, separator, canonical), true);
    }

    /// <summary>
    /// Removes the marker-delimited <c>.gitignore</c> block if present, leaving everything else byte-for-byte
    /// intact.
    /// </summary>
    /// <param name="content">Current <c>.gitignore</c> content, or null when the file does not exist.</param>
    /// <returns>The new content and whether the block was removed.</returns>
    public static (string Content, bool Removed) RemoveGitignoreBlock(string? content)
    {
        var current = content ?? string.Empty;
        var start = current.IndexOf(GitignoreBlockStart, StringComparison.Ordinal);

        if (start < 0)
        {
            return (current, false);
        }

        var end = current.IndexOf(GitignoreBlockEnd, start, StringComparison.Ordinal);
        var removeEnd = end >= 0 ? end + GitignoreBlockEnd.Length : current.Length;

        // Also swallow the newline that terminates the block so removal is exact.
        if (removeEnd < current.Length && current[removeEnd] == '\n')
        {
            removeEnd++;
        }

        return (string.Concat(current[..start], current[removeEnd..]), true);
    }

    /// <summary>The canonical <c>.gitignore</c> block (with trailing newline).</summary>
    public static string BuildGitignoreBlock()
    {
        var builder = new StringBuilder();
        builder.Append(GitignoreBlockStart).Append('\n');

        foreach (var entry in GitignoreBlockEntries)
        {
            builder.Append(entry).Append('\n');
        }

        builder.Append(GitignoreBlockEnd).Append('\n');
        return builder.ToString();
    }

    /// <summary>Start marker of the crontab block for one pipeline.</summary>
    /// <param name="repositorySlug">Slug identifying the repository.</param>
    /// <param name="pipelineSlug">Slug identifying the pipeline.</param>
    public static string GetCrontabBlockStart(string repositorySlug, string pipelineSlug) =>
        $"{CrontabMarkerPrefix} {repositorySlug}:{pipelineSlug} begin";

    /// <summary>End marker of the crontab block for one pipeline.</summary>
    /// <param name="repositorySlug">Slug identifying the repository.</param>
    /// <param name="pipelineSlug">Slug identifying the pipeline.</param>
    public static string GetCrontabBlockEnd(string repositorySlug, string pipelineSlug) =>
        $"{CrontabMarkerPrefix} {repositorySlug}:{pipelineSlug} end";

    /// <summary>True when a crontab line is a railway block marker.</summary>
    /// <param name="line">The crontab line to inspect.</param>
    public static bool IsCrontabMarkerLine(string line) =>
        line.TrimStart().StartsWith(CrontabMarkerPrefix, StringComparison.Ordinal);

    /// <summary>launchd label for a (repo, pipeline) schedule.</summary>
    /// <param name="repositorySlug">Slug identifying the repository.</param>
    /// <param name="pipelineSlug">Slug identifying the pipeline.</param>
    public static string GetLaunchdLabel(string repositorySlug, string pipelineSlug) =>
        $"com.railway.{repositorySlug}.{pipelineSlug}";

    /// <summary><c>schtasks</c> task name for a (repo, pipeline) schedule.</summary>
    /// <param name="repositorySlug">Slug identifying the repository.</param>
    /// <param name="pipelineSlug">Slug identifying the pipeline.</param>
    public static string GetSchtasksTaskName(string repositorySlug, string pipelineSlug) =>
        $@"Railway\{repositorySlug}\{pipelineSlug}";

    /// <summary>
    /// Describes how to invoke the railway CLI from a generated script, based on the running process
    /// shape (assumption A2): the dotnet host is recorded as <c>dotnet "&lt;abs&gt;/Cli.dll"</c>, a native
    /// apphost is recorded verbatim.
    /// </summary>
    /// <param name="processPath">Value of <see cref="Environment.ProcessPath"/>.</param>
    /// <param name="entryAssemblyLocation">Location of the entry assembly (e.g. <c>Cli.dll</c>), or null/empty when unavailable.</param>
    public static HookLaunchCommand DescribeLauncher(string processPath, string? entryAssemblyLocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);

        var isDotnetHost = string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            DotnetHostName,
            StringComparison.OrdinalIgnoreCase);

        if (isDotnetHost && !string.IsNullOrEmpty(entryAssemblyLocation))
        {
            return new HookLaunchCommand(DotnetHostName, [entryAssemblyLocation]);
        }

        return new HookLaunchCommand(processPath, []);
    }
}

/// <summary>
/// A structured "how to launch the railway CLI" description: executable plus leading arguments
/// (e.g. the assembly path when running under the dotnet host).
/// </summary>
/// <param name="FileName">Executable to start (e.g. <c>dotnet</c> or an absolute apphost path).</param>
/// <param name="Arguments">Arguments that precede the command line (e.g. the CLI assembly path).</param>
public sealed record HookLaunchCommand(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>The command as a POSIX <c>sh</c>-quoted string (for embedding in wrapper scripts).</summary>
    public string ToShellCommand()
    {
        var builder = new StringBuilder(HookScriptHelper.QuoteSh(FileName));

        foreach (var argument in Arguments)
        {
            builder.Append(' ').Append(HookScriptHelper.QuoteSh(argument));
        }

        return builder.ToString();
    }

    /// <summary>Returns a copy with <paramref name="extraArguments"/> appended to <see cref="Arguments"/>.</summary>
    /// <param name="extraArguments">Arguments to append.</param>
    public HookLaunchCommand WithArguments(params string[] extraArguments) =>
        new(FileName, [.. Arguments, .. extraArguments]);
}

/// <summary>
/// Seam for capturing how to launch the railway CLI from generated scripts and detached hook runs,
/// so tests can pin both the dotnet-host and native-apphost shapes.
/// </summary>
public interface IHookLauncher
{
    /// <summary>Returns the launcher to embed/use for hook invocation.</summary>
    HookLaunchCommand GetLaunchCommand();
}

/// <summary>
/// Default <see cref="IHookLauncher"/>: inspects the current process to decide between the dotnet
/// host shape and the native apphost shape (assumption A2).
/// </summary>
public sealed class EnvironmentHookLauncher : IHookLauncher
{
    /// <inheritdoc />
    public HookLaunchCommand GetLaunchCommand()
    {
        var processPath = Environment.ProcessPath
            ?? Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("Unable to determine the railway launcher path.");

        var entryAssemblyLocation = Assembly.GetEntryAssembly()?.Location;

        return HookScriptHelper.DescribeLauncher(
            processPath,
            string.IsNullOrEmpty(entryAssemblyLocation) ? null : entryAssemblyLocation);
    }
}
