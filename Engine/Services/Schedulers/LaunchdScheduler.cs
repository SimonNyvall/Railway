using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Railway.Engine.Services.Interfaces;

namespace Railway.Engine.Services.Schedulers;

/// <summary>
/// Materializes schedule triggers as per-pipeline launchd agents under
/// <c>~/Library/LaunchAgents</c> (overridable through <see cref="RootEnvironmentVariable"/> or the
/// constructor). One agent per (pipeline, cron): the plist name embeds a stable hash of the canonical
/// expression so entries never shift when sibling schedules change.
/// </summary>
/// <remarks>
/// The generated agent runs <c>/bin/sh &lt;wrapper&gt;</c> with <c>RunAtLoad=false</c> and a
/// <c>StartCalendarInterval</c> array built from <see cref="CronSchedule.ExpandForLaunchd"/> (keys within
/// one dict are ANDed, dicts are ORed). Loading/unloading goes through <c>launchctl bootstrap</c>/
/// <c>bootout</c>; when <c>launchctl</c> is unavailable (CI, non-macOS) the plist is still written and the
/// outcome carries a warning instead of failing.
/// </remarks>
public sealed class LaunchdScheduler : SchedulerBase
{
    /// <summary>Environment variable overriding the LaunchAgents directory (tests point it at a temp directory).</summary>
    public const string RootEnvironmentVariable = "RAILWAY_HOOKS_LAUNCHD_ROOT";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly ISystemCommandRunner _runner;
    private readonly string _root;

    /// <summary>Initializes a new instance of the <see cref="LaunchdScheduler"/> class.</summary>
    /// <param name="paths">Resolves repository slugs, wrapper and log paths.</param>
    /// <param name="runner">Runs <c>id</c> and <c>launchctl</c>; faked by tests.</param>
    /// <param name="rootOverride">
    /// Explicit LaunchAgents directory (tests); when null the <see cref="RootEnvironmentVariable"/>
    /// environment variable is consulted, then <c>~/Library/LaunchAgents</c>.
    /// </param>
    /// <param name="environment">Environment reader; defaults to the process environment.</param>
    public LaunchdScheduler(
        HookPaths paths,
        ISystemCommandRunner runner,
        string? rootOverride = null,
        Func<string, string?>? environment = null)
        : base(paths)
    {
        _runner = runner;
        var readEnvironment = environment ?? ReadEnvironmentVariable;
        _root = rootOverride
            ?? readEnvironment(RootEnvironmentVariable)
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "LaunchAgents");
    }

    /// <inheritdoc />
    public override string Name => "launchd";

    /// <inheritdoc />
    public override bool UsesWrapperScript => true;

    /// <summary>Gets the LaunchAgents directory this scheduler reads and writes.</summary>
    public string Root => _root;

    /// <inheritdoc />
    public override bool IsSupported() => OperatingSystem.IsMacOS();

    /// <inheritdoc />
    public override IReadOnlyList<ScheduleListEntry> List(string repositoryPath)
    {
        var entries = new List<ScheduleListEntry>();

        if (!Directory.Exists(_root))
        {
            return entries;
        }

        var prefix = GetLabelPrefix(repositoryPath);

        foreach (var plistPath in Directory.EnumerateFiles(_root, prefix + "*.plist"))
        {
            if (TryReadMetadata(plistPath, out var pipelineFileName, out var cron))
            {
                entries.Add(new ScheduleListEntry(pipelineFileName, cron, plistPath));
            }
        }

        return entries;
    }

    /// <inheritdoc />
    public override ScheduleInstallOutcome Install(string repositoryPath, ScheduleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (label, plistPath) = ResolveEntry(repositoryPath, request);

        if (!TryParse(request.Cron, out var schedule, out var parseError))
        {
            return Failed(request, plistPath, DescribeUnsupported(request.PipelineFileName, request.Cron, parseError!, null));
        }

        IReadOnlyList<LaunchdCalendarEntry> calendar;

        try
        {
            calendar = schedule!.ExpandForLaunchd();
        }
        catch (CronExpressionException exception)
        {
            return Failed(request, plistPath, DescribeUnsupported(request.PipelineFileName, request.Cron, exception.Message, null));
        }

        // The plist redirects into .railway/logs; create it first or the agent cannot start.
        EnsureLogDirectory(repositoryPath);

        var expected = BuildPlist(
            label,
            GetScheduleWrapperScriptPath(repositoryPath, request.PipelineFileName),
            calendar,
            GetScheduleLogPath(repositoryPath, request.PipelineFileName),
            request.PipelineFileName,
            request.Cron);

        var action = ScheduleAction.Created;

        if (File.Exists(plistPath))
        {
            if (!TryReadMetadata(plistPath, out _, out _))
            {
                return Failed(
                    request,
                    plistPath,
                    $"an entry already exists at '{plistPath}' but is not managed by railway; refusing to overwrite it.");
            }

            action = string.Equals(File.ReadAllText(plistPath), expected, StringComparison.Ordinal)
                ? ScheduleAction.Unchanged
                : ScheduleAction.Updated;
        }

        if (action == ScheduleAction.Unchanged)
        {
            return new ScheduleInstallOutcome(request.PipelineFileName, request.Cron, plistPath, action);
        }

        Directory.CreateDirectory(_root);
        File.WriteAllText(plistPath, expected, Utf8NoBom);

        var warning = action == ScheduleAction.Updated
            ? Bootout(label)
            : null;

        var loadWarning = Bootstrap(plistPath, label);
        warning ??= loadWarning;

        return new ScheduleInstallOutcome(request.PipelineFileName, request.Cron, plistPath, action, null, warning);
    }

    /// <inheritdoc />
    public override ScheduleRemoveOutcome Remove(string repositoryPath, string pipelineFileName, string cronExpression)
    {
        var (label, plistPath) = ResolveEntry(repositoryPath, new ScheduleRequest(pipelineFileName, cronExpression));

        if (!File.Exists(plistPath))
        {
            return new ScheduleRemoveOutcome(pipelineFileName, cronExpression, plistPath, ScheduleAction.Unchanged);
        }

        if (!TryReadMetadata(plistPath, out _, out _))
        {
            return new ScheduleRemoveOutcome(
                pipelineFileName,
                cronExpression,
                plistPath,
                ScheduleAction.Failed,
                $"an entry exists at '{plistPath}' but is not managed by railway; leaving it in place.");
        }

        Bootout(label);

        try
        {
            File.Delete(plistPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ScheduleRemoveOutcome(
                pipelineFileName,
                cronExpression,
                plistPath,
                ScheduleAction.Failed,
                $"could not delete '{plistPath}': {exception.Message}");
        }

        return new ScheduleRemoveOutcome(pipelineFileName, cronExpression, plistPath, ScheduleAction.Removed);
    }

    /// <inheritdoc />
    public override ScheduleStatusEntry Inspect(string repositoryPath, ScheduleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (_, plistPath) = ResolveEntry(repositoryPath, request);

        if (!TryParse(request.Cron, out var schedule, out var parseError))
        {
            return new ScheduleStatusEntry(
                request.PipelineFileName,
                request.Cron,
                ScheduleState.Unsupported,
                null,
                DescribeUnsupported(request.PipelineFileName, request.Cron, parseError!, null));
        }

        IReadOnlyList<LaunchdCalendarEntry> calendar;

        try
        {
            calendar = schedule!.ExpandForLaunchd();
        }
        catch (CronExpressionException exception)
        {
            return new ScheduleStatusEntry(
                request.PipelineFileName,
                request.Cron,
                ScheduleState.Unsupported,
                null,
                DescribeUnsupported(request.PipelineFileName, request.Cron, exception.Message, null));
        }

        var samePipeline = List(repositoryPath)
            .Where(entry => string.Equals(entry.PipelineFileName, request.PipelineFileName, StringComparison.Ordinal))
            .ToArray();

        if (!File.Exists(plistPath) || !TryReadMetadata(plistPath, out _, out _))
        {
            return new ScheduleStatusEntry(
                request.PipelineFileName,
                request.Cron,
                samePipeline.Length > 0 ? ScheduleState.Drifted : ScheduleState.Missing,
                null);
        }

        var expected = BuildPlist(
            Path.GetFileNameWithoutExtension(plistPath),
            GetScheduleWrapperScriptPath(repositoryPath, request.PipelineFileName),
            calendar,
            GetScheduleLogPath(repositoryPath, request.PipelineFileName),
            request.PipelineFileName,
            request.Cron);

        var state = string.Equals(File.ReadAllText(plistPath), expected, StringComparison.Ordinal)
            ? ScheduleState.Installed
            : ScheduleState.Drifted;

        return new ScheduleStatusEntry(request.PipelineFileName, request.Cron, state, plistPath);
    }

    /// <summary>Builds the launchd plist for one (pipeline, cron) entry.</summary>
    /// <param name="label">The launchd label (also the plist base name).</param>
    /// <param name="wrapperPath">Absolute path of the generated wrapper the agent runs.</param>
    /// <param name="calendar">The <c>StartCalendarInterval</c> expansion.</param>
    /// <param name="logPath">Absolute log path used for both stdout and stderr.</param>
    /// <param name="pipelineFileName">Pipeline file name, recorded as provenance.</param>
    /// <param name="cronExpression">The schedule expression, recorded as provenance.</param>
    public static string BuildPlist(
        string label,
        string wrapperPath,
        IReadOnlyList<LaunchdCalendarEntry> calendar,
        string logPath,
        string pipelineFileName,
        string cronExpression)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        builder.Append("<plist version=\"1.0\">\n");
        builder.Append("<dict>\n");
        AppendKey(builder, "Label", label);
        builder.Append("  <key>ProgramArguments</key>\n");
        builder.Append("  <array>\n");
        builder.Append("    <string>/bin/sh</string>\n");
        AppendStringElement(builder, wrapperPath, "    ");
        // The wrapper forwards its first argument as --cron, so entries record their expression.
        AppendStringElement(builder, cronExpression, "    ");
        builder.Append("  </array>\n");
        builder.Append("  <key>RunAtLoad</key>\n");
        builder.Append("  <false/>\n");
        builder.Append("  <key>StartCalendarInterval</key>\n");
        builder.Append("  <array>\n");

        foreach (var entry in calendar)
        {
            AppendCalendarEntry(builder, entry);
        }

        builder.Append("  </array>\n");
        AppendKey(builder, "StandardOutPath", logPath);
        AppendKey(builder, "StandardErrorPath", logPath);
        AppendKey(builder, "RailwayPipeline", pipelineFileName);
        AppendKey(builder, "RailwayCron", cronExpression);
        builder.Append("</dict>\n");
        builder.Append("</plist>\n");
        return builder.ToString();
    }

    private static void AppendCalendarEntry(StringBuilder builder, LaunchdCalendarEntry entry)
    {
        builder.Append("    <dict>\n");

        AppendCalendarField(builder, "Minute", entry.Minutes, 60);
        AppendCalendarField(builder, "Hour", entry.Hours, 24);
        AppendCalendarField(builder, "DayOfMonth", entry.DaysOfMonth, 31);
        AppendCalendarField(builder, "Month", entry.Months, 12);
        AppendCalendarField(builder, "DayOfWeek", entry.DaysOfWeek, 7);

        builder.Append("    </dict>\n");
    }

    private static void AppendCalendarField(StringBuilder builder, string key, IReadOnlyList<int> values, int fullCount)
    {
        // launchd treats an absent key as "every value"; wildcard fields are therefore omitted so the
        // generated plist matches the cron expression exactly.
        if (values.Count >= fullCount)
        {
            return;
        }

        builder.Append("      <key>").Append(key).Append("</key>\n");

        if (values.Count == 1)
        {
            builder.Append("      <integer>")
                .Append(values[0].ToString(CultureInfo.InvariantCulture))
                .Append("</integer>\n");
            return;
        }

        builder.Append("      <array>\n");

        foreach (var value in values)
        {
            builder.Append("        <integer>")
                .Append(value.ToString(CultureInfo.InvariantCulture))
                .Append("</integer>\n");
        }

        builder.Append("      </array>\n");
    }

    private static void AppendKey(StringBuilder builder, string key, string value)
    {
        builder.Append("  <key>").Append(key).Append("</key>\n");
        AppendStringElement(builder, value, "  ");
    }

    private static void AppendStringElement(StringBuilder builder, string value, string indent) =>
        builder.Append(indent).Append("<string>").Append(SecurityElement.Escape(value)).Append("</string>\n");

    private (string Label, string Path) ResolveEntry(string repositoryPath, ScheduleRequest request)
    {
        var label = GetLabel(repositoryPath, request);
        return (label, System.IO.Path.Combine(_root, label + ".plist"));
    }

    private string GetLabel(string repositoryPath, ScheduleRequest request) =>
        HookScriptHelper.GetLaunchdLabel(Paths.GetRepositorySlug(repositoryPath), Paths.GetPipelineSlug(request.PipelineFileName))
        + "."
        + GetEntrySuffix(request.Cron);

    private string GetLabelPrefix(string repositoryPath) =>
        $"com.railway.{Paths.GetRepositorySlug(repositoryPath)}.";

    private ScheduleInstallOutcome Failed(ScheduleRequest request, string entryName, string error) =>
        new(request.PipelineFileName, request.Cron, entryName, ScheduleAction.Failed, error);

    /// <summary>Runs <c>launchctl bootstrap</c>, returning a warning instead of throwing when it is unavailable.</summary>
    /// <param name="plistPath">Absolute plist path.</param>
    /// <param name="label">The launchd label.</param>
    private string? Bootstrap(string plistPath, string label)
    {
        var uid = ResolveUid();

        if (uid is null)
        {
            return $"launchctl is not available; wrote '{plistPath}' but did not load it."
                + $" Load it with: launchctl bootstrap gui/<uid> {HookScriptHelper.QuoteSh(plistPath)}";
        }

        var result = _runner.Run("launchctl", ["bootstrap", $"gui/{uid}", plistPath]);

        if (result.ExitCode == SystemCommandResult.ExecutableNotFoundExitCode)
        {
            return $"launchctl is not available; wrote '{plistPath}' but did not load it.";
        }

        return result.ExitCode == 0
            ? null
            : $"launchctl bootstrap {label} exited {result.ExitCode}: {FirstLine(result.StandardError)}";
    }

    /// <summary>Runs <c>launchctl bootout</c>, ignoring failures (best effort).</summary>
    /// <param name="label">The launchd label.</param>
    private string? Bootout(string label)
    {
        var uid = ResolveUid();

        if (uid is null)
        {
            return null;
        }

        var result = _runner.Run("launchctl", ["bootout", $"gui/{uid}/{label}"]);

        return result.ExitCode is 0 or SystemCommandResult.ExecutableNotFoundExitCode
            ? null
            : $"launchctl bootout {label} exited {result.ExitCode}: {FirstLine(result.StandardError)}";
    }

    private string? ResolveUid()
    {
        var result = _runner.Run("id", ["-u"]);

        if (result.ExitCode != 0)
        {
            return null;
        }

        var text = FirstLine(result.StandardOutput);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid)
            ? uid.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static bool TryReadMetadata(string plistPath, out string pipelineFileName, out string cron)
    {
        pipelineFileName = string.Empty;
        cron = string.Empty;

        try
        {
            // The generated plist declares the Apple DTD; ignore it (never resolve external entities).
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };

            using var reader = XmlReader.Create(plistPath, settings);
            var document = XDocument.Load(reader);
            var dict = document.Root?.Element("dict");

            if (dict is null)
            {
                return false;
            }

            var label = ReadStringValue(dict, "Label");

            if (label is null || !label.StartsWith("com.railway.", StringComparison.Ordinal))
            {
                return false;
            }

            pipelineFileName = ReadStringValue(dict, "RailwayPipeline") ?? string.Empty;
            cron = ReadStringValue(dict, "RailwayCron") ?? string.Empty;

            return pipelineFileName.Length > 0 && cron.Length > 0;
        }
        catch (Exception exception) when (exception is IOException or XmlException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? ReadStringValue(XElement dict, string key)
    {
        XElement? lookingForValue = null;

        foreach (var child in dict.Elements())
        {
            if (lookingForValue is not null && child.Name == "string")
            {
                return child.Value;
            }

            lookingForValue = (child.Name == "key" && child.Value == key) ? child : null;
        }

        return null;
    }
}
