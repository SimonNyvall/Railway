using System.Globalization;
using System.Text;

using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Railway.Engine.Services.Interfaces;

namespace Railway.Engine.Services.Schedulers;

/// <summary>
/// Materializes schedule triggers as Windows scheduled tasks via <c>schtasks</c>, one task per
/// (pipeline, cron) named <c>Railway\&lt;repoSlug&gt;\&lt;pipelineSlug&gt;.&lt;hash&gt;</c>
/// (see <see cref="HookScriptHelper.GetSchtasksTaskName"/>).
/// </summary>
/// <remarks>
/// <para>
/// Ownership is <b>name based</b>: a task is ours when its name lies under the
/// <c>Railway\&lt;repoSlug&gt;\</c> folder and its leaf is <c>&lt;pipelineSlug&gt;.&lt;hash&gt;</c>
/// (slug <c>[a-z0-9-]+</c>, hash 8 lowercase hex). Tasks that do not match that shape are foreign and
/// are never listed, overwritten or deleted. This is deliberately the only provenance we rely on
/// (<c>schtasks /Create</c> has no description switch, so a marker written into a description would
/// never be readable back).
/// </para>
/// <para>
/// <see cref="List"/> therefore queries every task on the machine (<c>schtasks /Query /FO LIST</c>)
/// and filters the output by that shape, because <c>schtasks</c> has no documented wildcard or
/// folder-only listing (folder listing is undocumented; <c>/TN</c> matches a single exact task).
/// A consequence of name-only provenance is that the task's <c>/TR</c> command is not compared, so an
/// existing task at the exact name is reported <see cref="ScheduleAction.Unchanged"/> even if an older
/// install wrote a different command.
/// </para>
/// <para>
/// This path is independent of the git-hook <c>sh</c> chain: the task runs the railway launcher
/// directly (no POSIX wrapper), redirecting stdout/stderr to
/// <c>.railway/logs/schedule-&lt;slug&gt;.log</c> through <c>cmd.exe</c>. Task Scheduler launches the
/// <c>/TR</c> program without a shell, so the invocation is wrapped in <c>cmd /c "…"</c> for the
/// redirect to be interpreted; the <c>/TR</c> value is validated against its 262-character limit at
/// install time and fails loudly when exceeded.
/// </para>
/// <para>
/// Documented cron → <c>/SC</c> mapping (everything else fails loudly):
/// </para>
/// <list type="bullet">
/// <item><description><c>*/n * * * *</c> → <c>/SC MINUTE /MO n</c></description></item>
/// <item><description><c>m h * * *</c> → <c>/SC DAILY /MO 1 /ST hh:mm</c></description></item>
/// <item><description><c>m h * * dow[,dow…]</c> → <c>/SC WEEKLY /MO 1 /D MON,TUE /ST hh:mm</c></description></item>
/// <item><description><c>m h d * *</c> → <c>/SC MONTHLY /MO 1 /D d /ST hh:mm</c></description></item>
/// </list>
/// </remarks>
public sealed class SchtasksScheduler : SchedulerBase
{
    /// <summary>Maximum length of the <c>/TR</c> value accepted by <c>schtasks</c>.</summary>
    public const int MaxTaskRunLength = 262;

    private const string TaskFolderPrefix = "Railway";

    private static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private readonly ISystemCommandRunner _runner;
    private readonly IHookLauncher _launcher;

    /// <summary>Initializes a new instance of the <see cref="SchtasksScheduler"/> class.</summary>
    /// <param name="paths">Resolves repository slugs and log paths.</param>
    /// <param name="runner">Runs <c>schtasks</c>; faked by tests.</param>
    /// <param name="launcher">Supplies the command the task runs.</param>
    public SchtasksScheduler(HookPaths paths, ISystemCommandRunner runner, IHookLauncher launcher)
        : base(paths)
    {
        _runner = runner;
        _launcher = launcher;
    }

    /// <inheritdoc />
    public override string Name => "schtasks";

    /// <inheritdoc />
    public override bool UsesWrapperScript => false;

    /// <inheritdoc />
    public override bool IsSupported() => OperatingSystem.IsWindows();

    /// <inheritdoc />
    /// <remarks>
    /// The only provenance recoverable from a task is its name, so entries are reported with the
    /// pipeline <i>slug</i> as <see cref="ScheduleListEntry.PipelineFileName"/> and the entry hash as
    /// <see cref="ScheduleListEntry.Cron"/>; consumers that need the declared identity compare slugs
    /// (see <see cref="Reconcile"/> and <see cref="Inspect"/>).
    /// </remarks>
    public override IReadOnlyList<ScheduleListEntry> List(string repositoryPath)
    {
        var entries = new List<ScheduleListEntry>();
        var folder = GetFolder(repositoryPath);
        var result = _runner.Run("schtasks", ["/Query", "/FO", "LIST"]);

        if (result.ExitCode != 0)
        {
            return entries;
        }

        foreach (var taskName in ReadTaskNames(result.StandardOutput))
        {
            if (TryParseOwnedTaskName(taskName, folder, out var pipelineSlug, out var suffix))
            {
                entries.Add(new ScheduleListEntry(pipelineSlug, suffix, taskName));
            }
        }

        return entries;
    }

    /// <inheritdoc />
    public override ScheduleInstallOutcome Install(string repositoryPath, ScheduleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entryName = ResolveEntryName(repositoryPath, request);

        if (!TryParse(request.Cron, out var schedule, out var parseError))
        {
            return Failed(request, entryName, DescribeUnsupported(request.PipelineFileName, request.Cron, parseError!, null));
        }

        if (!TryMap(schedule!, out var switchArguments, out var reason, out var suggestion))
        {
            return Failed(request, entryName, DescribeUnsupported(request.PipelineFileName, request.Cron, reason!, suggestion));
        }

        // The task redirects into .railway/logs; create it first so the scheduled command can start.
        EnsureLogDirectory(repositoryPath);

        var command = BuildTaskCommand(repositoryPath, request.PipelineFileName, request.Cron);

        if (command.Length > MaxTaskRunLength)
        {
            return Failed(
                request,
                entryName,
                DescribeUnsupported(
                    request.PipelineFileName,
                    request.Cron,
                    $"the generated command is {command.Length} characters long, exceeding the {MaxTaskRunLength}-character /TR limit",
                    null));
        }

        var action = TaskExists(entryName) ? ScheduleAction.Unchanged : ScheduleAction.Created;

        if (action == ScheduleAction.Unchanged)
        {
            return new ScheduleInstallOutcome(request.PipelineFileName, request.Cron, entryName, action);
        }

        var arguments = new List<string> { "/Create", "/TN", entryName, "/TR", command, "/SC" };
        arguments.AddRange(switchArguments);
        arguments.Add("/F");

        var result = _runner.Run("schtasks", arguments);

        if (result.ExitCode == SystemCommandResult.ExecutableNotFoundExitCode)
        {
            return Failed(request, entryName, "schtasks is not available on this machine");
        }

        return result.ExitCode == 0
            ? new ScheduleInstallOutcome(request.PipelineFileName, request.Cron, entryName, action)
            : Failed(request, entryName, $"schtasks /Create exited {result.ExitCode}: {FirstLine(result.StandardError)}");
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ownership is name based, so a task is only removed when its name matches the
    /// <c>Railway\&lt;repoSlug&gt;\&lt;pipelineSlug&gt;.&lt;hash&gt;</c> shape; anything else is foreign.
    /// </remarks>
    public override ScheduleRemoveOutcome Remove(string repositoryPath, string pipelineFileName, string cronExpression)
    {
        var entryName = ResolveEntryName(repositoryPath, new ScheduleRequest(pipelineFileName, cronExpression));
        var folder = GetFolder(repositoryPath);

        if (!TaskExists(entryName))
        {
            return new ScheduleRemoveOutcome(pipelineFileName, cronExpression, entryName, ScheduleAction.Unchanged);
        }

        if (!TryParseOwnedTaskName(entryName, folder, out _, out _))
        {
            return new ScheduleRemoveOutcome(
                pipelineFileName,
                cronExpression,
                entryName,
                ScheduleAction.Failed,
                $"a scheduled task named '{entryName}' is not in the railway task namespace; leaving it in place.");
        }

        return DeleteTask(entryName, pipelineFileName, cronExpression);
    }

    /// <summary>
    /// Installs every desired entry and removes listed owned tasks that are no longer desired. It is
    /// name based (rather than the base class' <c>(pipeline file name, cron)</c> keys) because a task
    /// name is the only provenance recoverable from <c>schtasks</c>.
    /// </summary>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    /// <param name="desired">The full desired schedule set (empty removes every owned entry).</param>
    public override ScheduleReconcileResult Reconcile(string repositoryPath, IReadOnlyList<ScheduleRequest> desired)
    {
        ArgumentNullException.ThrowIfNull(desired);

        var entries = new List<ScheduleInstallOutcome>();
        var desiredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var request in desired)
        {
            desiredNames.Add(ResolveEntryName(repositoryPath, request));
            entries.Add(Install(repositoryPath, request));
        }

        foreach (var existing in List(repositoryPath))
        {
            if (!desiredNames.Contains(existing.EntryName))
            {
                var removed = DeleteTask(existing.EntryName, existing.PipelineFileName, existing.Cron);

                entries.Add(new ScheduleInstallOutcome(
                    removed.PipelineFileName,
                    removed.Cron,
                    removed.EntryName,
                    removed.Action,
                    removed.Error));
            }
        }

        return new ScheduleReconcileResult(entries);
    }

    /// <inheritdoc />
    public override ScheduleStatusEntry Inspect(string repositoryPath, ScheduleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entryName = ResolveEntryName(repositoryPath, request);

        if (!TryParse(request.Cron, out var schedule, out var parseError))
        {
            return new ScheduleStatusEntry(
                request.PipelineFileName,
                request.Cron,
                ScheduleState.Unsupported,
                null,
                DescribeUnsupported(request.PipelineFileName, request.Cron, parseError!, null));
        }

        if (!TryMap(schedule!, out _, out var reason, out var suggestion))
        {
            return new ScheduleStatusEntry(
                request.PipelineFileName,
                request.Cron,
                ScheduleState.Unsupported,
                null,
                DescribeUnsupported(request.PipelineFileName, request.Cron, reason!, suggestion));
        }

        if (TaskExists(entryName))
        {
            return new ScheduleStatusEntry(request.PipelineFileName, request.Cron, ScheduleState.Installed, entryName);
        }

        var pipelineSlug = Paths.GetPipelineSlug(request.PipelineFileName);
        var samePipeline = List(repositoryPath)
            .Any(entry => string.Equals(entry.PipelineFileName, pipelineSlug, StringComparison.Ordinal));

        return new ScheduleStatusEntry(
            request.PipelineFileName,
            request.Cron,
            samePipeline ? ScheduleState.Drifted : ScheduleState.Missing,
            null);
    }

    /// <summary>Maps a parsed cron schedule to <c>schtasks /SC</c> arguments.</summary>
    /// <param name="schedule">The parsed schedule.</param>
    /// <param name="arguments">The <c>/SC …</c> argument vector when the mapping succeeds.</param>
    /// <param name="reason">Why the schedule cannot be mapped when it cannot.</param>
    /// <param name="suggestion">A nearest mappable equivalent expression, or null.</param>
    private static bool TryMap(CronSchedule schedule, out List<string> arguments, out string? reason, out string? suggestion)
    {
        arguments = [];
        reason = null;
        suggestion = null;

        var minutesStep = GetUniformStep(schedule.Minutes, 60);

        if (minutesStep is not null
            && IsFull(schedule.Hours, 24)
            && IsFull(schedule.DaysOfMonth, 31)
            && IsFull(schedule.Months, 12)
            && IsFull(schedule.DaysOfWeek, 7))
        {
            arguments = ["MINUTE", "/MO", minutesStep.Value.ToString(CultureInfo.InvariantCulture)];
            return true;
        }

        var singleMinute = schedule.Minutes.Count == 1;
        var singleHour = schedule.Hours.Count == 1;

        if (singleMinute && singleHour && IsFull(schedule.Months, 12))
        {
            var time = $"{schedule.Hours[0]:D2}:{schedule.Minutes[0]:D2}";

            if (IsFull(schedule.DaysOfMonth, 31) && IsFull(schedule.DaysOfWeek, 7))
            {
                arguments = ["DAILY", "/MO", "1", "/ST", time];
                return true;
            }

            if (IsFull(schedule.DaysOfMonth, 31) && !IsFull(schedule.DaysOfWeek, 7))
            {
                arguments = ["WEEKLY", "/MO", "1", "/D", string.Join(',', schedule.DaysOfWeek.Select(day => DayNames[day])), "/ST", time];
                return true;
            }

            if (IsFull(schedule.DaysOfWeek, 7) && schedule.DaysOfMonth.Count == 1)
            {
                arguments = ["MONTHLY", "/MO", "1", "/D", schedule.DaysOfMonth[0].ToString(CultureInfo.InvariantCulture), "/ST", time];
                return true;
            }
        }

        reason = "the expression does not map to a single /SC schedule (MINUTE steps, or DAILY/WEEKLY/MONTHLY at one fixed time)";

        if (singleMinute && singleHour)
        {
            suggestion = $"'{schedule.Minutes[0]} {schedule.Hours[0]} * * *' (/SC DAILY /MO 1 /ST {schedule.Hours[0]:D2}:{schedule.Minutes[0]:D2})";
        }

        return false;
    }

    /// <summary>Detects a <c>*/n</c>-style minute field (values <c>0, n, 2n, …</c> filling the range).</summary>
    private static int? GetUniformStep(IReadOnlyList<int> values, int fullCount)
    {
        if (values.Count == 0 || values[0] != 0)
        {
            return null;
        }

        if (values.Count == fullCount)
        {
            return 1;
        }

        if (values.Count < 2)
        {
            return null;
        }

        var step = values[1] - values[0];

        if (step < 1)
        {
            return null;
        }

        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] != index * step)
            {
                return null;
            }
        }

        return values[^1] + step == fullCount ? step : null;
    }

    private static bool IsFull(IReadOnlyList<int> values, int fullCount) => values.Count >= fullCount;

    /// <summary>The <c>/TR</c> command line: the launcher, the schedule invocation (with its cron for
    /// provenance) and log redirection, wrapped in <c>cmd /c "…"</c> so the shell interprets the redirect.</summary>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    /// <param name="pipelineFileName">Pipeline file name.</param>
    /// <param name="cronExpression">The schedule expression, forwarded as <c>--cron</c> provenance.</param>
    private string BuildTaskCommand(string repositoryPath, string pipelineFileName, string cronExpression)
    {
        var launcher = _launcher.GetLaunchCommand();
        var builder = new StringBuilder();

        // Task Scheduler launches the program directly (no shell), so an unwrapped ">> log 2>&1" would
        // be literal argv. `cmd /c ""exe" args … >> log 2>&1"` runs the exe through cmd so it redirects.
        builder.Append("cmd /c ");
        builder.Append('"');
        builder.Append(QuoteCmd(launcher.FileName));

        foreach (var argument in launcher.Arguments)
        {
            builder.Append(' ').Append(QuoteCmd(argument));
        }

        builder.Append(" hooks run schedule --pipeline ").Append(QuoteCmd(pipelineFileName));
        builder.Append(" --path ").Append(QuoteCmd(Path.GetFullPath(repositoryPath)));
        builder.Append(" --cron ").Append(QuoteCmd(cronExpression));
        builder.Append(" >> ").Append(QuoteCmd(GetScheduleLogPath(repositoryPath, pipelineFileName))).Append(" 2>&1");
        builder.Append('"');

        return builder.ToString();
    }

    /// <summary>
    /// Quotes a token for a <c>cmd.exe</c> command line. Every token is quoted (even without whitespace)
    /// so shell metacharacters such as <c>&amp;</c> and <c>^</c> are passed literally; embedded quotes are
    /// doubled.
    /// </summary>
    private static string QuoteCmd(string value) =>
        value.Length == 0 ? "\"\"" : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private string GetFolder(string repositoryPath) =>
        $"{TaskFolderPrefix}\\{Paths.GetRepositorySlug(repositoryPath)}";

    /// <summary>
    /// The exact <c>schtasks</c> task name (also its <c>/TN</c> value and the entry identity) for a
    /// (pipeline, cron) request: <c>Railway\&lt;repoSlug&gt;\&lt;pipelineSlug&gt;.&lt;entryHash&gt;</c>.
    /// </summary>
    private string ResolveEntryName(string repositoryPath, ScheduleRequest request)
    {
        var repositorySlug = Paths.GetRepositorySlug(repositoryPath);
        var pipelineSlug = Paths.GetPipelineSlug(request.PipelineFileName);
        var suffix = GetEntrySuffix(request.Cron);
        return $"{HookScriptHelper.GetSchtasksTaskName(repositorySlug, pipelineSlug)}.{suffix}";
    }

    /// <summary>True when a task with exactly this name exists (regardless of who created it).</summary>
    private bool TaskExists(string taskName) =>
        _runner.Run("schtasks", ["/Query", "/TN", taskName]).ExitCode == 0;

    /// <summary>
    /// Deletes the task at exactly <paramref name="taskName"/> (the task name doubles as the entry
    /// identity). The caller is responsible for confirming the name is owned (<see cref="Remove"/>
    /// guards foreign names; <see cref="List"/> only yields owned shapes).
    /// </summary>
    private ScheduleRemoveOutcome DeleteTask(string taskName, string pipelineFileName, string cronExpression)
    {
        var result = _runner.Run("schtasks", ["/Delete", "/TN", taskName, "/F"]);

        if (result.ExitCode == SystemCommandResult.ExecutableNotFoundExitCode)
        {
            return new ScheduleRemoveOutcome(pipelineFileName, cronExpression, taskName, ScheduleAction.Failed, "schtasks is not available on this machine");
        }

        return result.ExitCode == 0
            ? new ScheduleRemoveOutcome(pipelineFileName, cronExpression, taskName, ScheduleAction.Removed)
            : new ScheduleRemoveOutcome(
                pipelineFileName,
                cronExpression,
                taskName,
                ScheduleAction.Failed,
                $"schtasks /Delete exited {result.ExitCode}: {FirstLine(result.StandardError)}");
    }

    /// <summary>Extracts the <c>TaskName</c> values from <c>schtasks /Query /FO LIST</c> output.</summary>
    private static IEnumerable<string> ReadTaskNames(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("TaskName:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line["TaskName:".Length..].Trim().TrimStart('\\');

            if (value.Length > 0)
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="taskName"/> lies under <paramref name="folder"/> and its leaf is
    /// <c>&lt;pipelineSlug&gt;.&lt;8 hex&gt;</c> — the shape every railway task has. Anything else is a
    /// foreign task that must never be touched.
    /// </summary>
    private static bool TryParseOwnedTaskName(string taskName, string folder, out string pipelineSlug, out string suffix)
    {
        pipelineSlug = string.Empty;
        suffix = string.Empty;

        var prefix = folder.TrimStart('\\') + "\\";
        var normalized = taskName.TrimStart('\\');

        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var leaf = normalized[prefix.Length..];
        var dot = leaf.LastIndexOf('.');

        if (dot <= 0)
        {
            return false;
        }

        var candidateSlug = leaf[..dot];
        var candidateSuffix = leaf[(dot + 1)..];

        if (!IsPipelineSlug(candidateSlug) || !IsEntrySuffix(candidateSuffix))
        {
            return false;
        }

        pipelineSlug = candidateSlug;
        suffix = candidateSuffix;
        return true;
    }

    private static bool IsPipelineSlug(string value) =>
        value.Length > 0 && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');

    private static bool IsEntrySuffix(string value) =>
        value.Length == 8 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private ScheduleInstallOutcome Failed(ScheduleRequest request, string entryName, string error) =>
        new(request.PipelineFileName, request.Cron, entryName, ScheduleAction.Failed, error);
}
