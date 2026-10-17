using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Railway.Engine.Helpers;
using Railway.Engine.Services.Interfaces;

namespace Railway.Engine.Services.Schedulers;

/// <summary>
/// Shared behavior for <see cref="IScheduler"/> implementations: cron validation/normalization,
/// stable entry naming, wrapper/log path resolution and the generic reconcile loop. Machine access is
/// always delegated to <see cref="Infrastructure.ISystemCommandRunner"/> by the derived types.
/// </summary>
public abstract class SchedulerBase : IScheduler
{
    /// <summary>
    /// Base directories baked into the wrapper script's <c>PATH</c> so a POSIX scheduler's minimal
    /// environment can still find the launcher when it is recorded as a bare executable name.
    /// </summary>
    protected static readonly IReadOnlyList<string> DefaultPathDirectories =
        ["/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"];

    /// <summary>Initializes a new instance of the <see cref="SchedulerBase"/> class.</summary>
    /// <param name="paths">Resolves repository slugs, wrapper and log paths.</param>
    protected SchedulerBase(HookPaths paths)
    {
        Paths = paths;
    }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract bool UsesWrapperScript { get; }

    /// <inheritdoc />
    public abstract bool IsSupported();

    /// <inheritdoc />
    public abstract IReadOnlyList<ScheduleListEntry> List(string repositoryPath);

    /// <inheritdoc />
    public abstract ScheduleInstallOutcome Install(string repositoryPath, ScheduleRequest request);

    /// <inheritdoc />
    public abstract ScheduleRemoveOutcome Remove(string repositoryPath, string pipelineFileName, string cronExpression);

    /// <inheritdoc />
    public abstract ScheduleStatusEntry Inspect(string repositoryPath, ScheduleRequest request);

    /// <inheritdoc />
    /// <remarks>
    /// The default implementation is a simple two-phase loop — install every desired entry, then remove
    /// owned entries whose (pipeline, cron) is no longer desired — which is correct for every scheduler in
    /// this assembly. It is virtual so a backend with a natural batch primitive can replace it.
    /// </remarks>
    public virtual ScheduleReconcileResult Reconcile(string repositoryPath, IReadOnlyList<ScheduleRequest> desired)
    {
        ArgumentNullException.ThrowIfNull(desired);

        var entries = new List<ScheduleInstallOutcome>();
        var desiredKeys = new HashSet<(string Pipeline, string Cron)>();

        foreach (var request in desired)
        {
            if (TryNormalize(request.Cron, out var normalized))
            {
                desiredKeys.Add((request.PipelineFileName, normalized));
            }

            entries.Add(Install(repositoryPath, request));
        }

        foreach (var existing in List(repositoryPath))
        {
            if (!TryNormalize(existing.Cron, out var normalized)
                || !desiredKeys.Contains((existing.PipelineFileName, normalized)))
            {
                var removed = Remove(repositoryPath, existing.PipelineFileName, existing.Cron);
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

    /// <summary>Gets the resolver for repository slugs, wrapper and log paths.</summary>
    protected HookPaths Paths { get; }

    /// <summary>
    /// Parses and canonically formats a cron expression (<c>minutes hours day-of-month month day-of-week</c>),
    /// with restricted fields expanded and wildcard fields written as <c>*</c>. Aliases such as
    /// <c>@daily</c> normalize to their 5-field equivalent.
    /// </summary>
    /// <param name="expression">The cron expression to normalize.</param>
    /// <exception cref="CronExpressionException">The expression is invalid or unsupported.</exception>
    public static string NormalizeExpression(string expression) => FormatSchedule(CronExpressionHelper.Parse(expression));

    /// <summary>Canonically formats an already-parsed <see cref="CronSchedule"/>.</summary>
    /// <param name="schedule">The parsed schedule.</param>
    public static string FormatSchedule(CronSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return string.Join(
            ' ',
            FormatField(schedule.Minutes, 60),
            FormatField(schedule.Hours, 24),
            FormatField(schedule.DaysOfMonth, 31),
            FormatField(schedule.Months, 12),
            FormatField(schedule.DaysOfWeek, 7));
    }

    /// <summary>
    /// A stable, filename-safe suffix for a cron expression: the first 8 hex characters of the SHA-256 of
    /// its canonical form. Stable across runs and independent of declaration order, so entry names never
    /// shift when another schedule of the same pipeline is added or removed.
    /// </summary>
    /// <param name="cronExpression">The cron expression (any supported form).</param>
    public static string GetCronHash(string cronExpression)
    {
        var normalized = NormalizeExpression(cronExpression);
        return HashText(normalized);
    }

    /// <summary>
    /// Entry-name suffix for an expression: its canonical hash when parseable, otherwise a hash of the
    /// raw text (so even an invalid expression resolves to a deterministic entry identity for reporting).
    /// </summary>
    /// <param name="cronExpression">The cron expression.</param>
    public static string GetEntrySuffix(string cronExpression) =>
        TryNormalize(cronExpression, out var normalized) ? HashText(normalized) : HashText(cronExpression);

    private static string HashText(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// Builds the failure message for an expression a scheduler cannot represent. It always names the
    /// pipeline, the expression and the scheduler, and appends the suggested equivalent when one exists.
    /// </summary>
    /// <param name="pipelineFileName">Pipeline file name.</param>
    /// <param name="cronExpression">The offending expression.</param>
    /// <param name="reason">Why the expression cannot be represented.</param>
    /// <param name="suggestedEquivalent">An equivalent expression the scheduler does support, or null.</param>
    protected string DescribeUnsupported(string pipelineFileName, string cronExpression, string reason, string? suggestedEquivalent)
    {
        var message = $"Schedule '{cronExpression}' for pipeline '{pipelineFileName}' cannot be represented by {Name}: {reason}";
        return suggestedEquivalent is null ? message : $"{message} Nearest equivalent: {suggestedEquivalent}.";
    }

    /// <summary>Parses a cron expression, reporting failure instead of throwing.</summary>
    /// <param name="cronExpression">The expression to parse.</param>
    /// <param name="schedule">The parsed schedule when parsing succeeded.</param>
    /// <param name="error">The failure message when parsing failed.</param>
    protected static bool TryParse(string? cronExpression, out CronSchedule? schedule, out string? error)
    {
        schedule = null;
        error = null;

        if (string.IsNullOrWhiteSpace(cronExpression))
        {
            error = "the expression is empty";
            return false;
        }

        try
        {
            schedule = CronExpressionHelper.Parse(cronExpression);
            return true;
        }
        catch (CronExpressionException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>Normalizes an expression, reporting failure instead of throwing.</summary>
    /// <param name="cronExpression">The expression to normalize.</param>
    /// <param name="normalized">The canonical form when normalization succeeded.</param>
    protected static bool TryNormalize(string? cronExpression, out string normalized)
    {
        normalized = string.Empty;

        try
        {
            normalized = NormalizeExpression(cronExpression!);
            return true;
        }
        catch (CronExpressionException)
        {
            return false;
        }
    }

    /// <summary>Absolute path of the generated per-pipeline schedule wrapper (<c>.railway/hooks/schedule-&lt;slug&gt;.sh</c>).</summary>
    /// <param name="paths">Hook path resolver.</param>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    /// <param name="pipelineFileName">Pipeline file name.</param>
    public static string ResolveScheduleWrapperScriptPath(HookPaths paths, string repositoryPath, string pipelineFileName) =>
        Path.Combine(
            paths.GetHooksDirectory(repositoryPath),
            HookScriptHelper.GetScheduleWrapperScriptName(pipelineFileName));

    /// <summary>Absolute path of the per-pipeline scheduler log (<c>.railway/logs/schedule-&lt;slug&gt;.log</c>).</summary>
    /// <param name="paths">Hook path resolver.</param>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    /// <param name="pipelineFileName">Pipeline file name.</param>
    public static string ResolveScheduleLogPath(HookPaths paths, string repositoryPath, string pipelineFileName) =>
        Path.Combine(
            paths.GetLogsDirectory(repositoryPath),
            $"schedule-{paths.GetPipelineSlug(pipelineFileName)}.log");

    /// <summary>Absolute path of the generated per-pipeline schedule wrapper (<c>.railway/hooks/schedule-&lt;slug&gt;.sh</c>).</summary>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    /// <param name="pipelineFileName">Pipeline file name.</param>
    protected string GetScheduleWrapperScriptPath(string repositoryPath, string pipelineFileName) =>
        ResolveScheduleWrapperScriptPath(Paths, repositoryPath, pipelineFileName);

    /// <summary>Absolute path of the per-pipeline scheduler log (<c>.railway/logs/schedule-&lt;slug&gt;.log</c>).</summary>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    /// <param name="pipelineFileName">Pipeline file name.</param>
    protected string GetScheduleLogPath(string repositoryPath, string pipelineFileName) =>
        ResolveScheduleLogPath(Paths, repositoryPath, pipelineFileName);

    /// <summary>
    /// Ensures the repository's log directory (<c>.railway/logs</c>) exists so a scheduler entry's
    /// stdout/stderr redirect target can be opened. POSIX <c>sh</c> refuses to run a command whose
    /// redirect target directory is missing, so a schedule would silently never fire without this.
    /// </summary>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    protected void EnsureLogDirectory(string repositoryPath) =>
        Directory.CreateDirectory(Paths.GetLogsDirectory(repositoryPath));

    /// <summary>Reads an environment variable, or null when unset.</summary>
    /// <param name="name">Variable name.</param>
    protected static string? ReadEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    /// <summary>First non-empty, trimmed line of captured command output.</summary>
    /// <param name="value">Captured standard output or error.</param>
    protected static string FirstLine(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
        ?? string.Empty;

    private static string FormatField(IReadOnlyList<int> values, int fullCount)
    {
        if (values.Count >= fullCount)
        {
            return "*";
        }

        var builder = new StringBuilder();

        foreach (var value in values)
        {
            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            builder.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
