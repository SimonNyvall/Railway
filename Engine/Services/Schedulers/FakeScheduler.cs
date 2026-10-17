using System.Text.Json;

using Railway.Engine.Helpers;
using Railway.Engine.Services.Interfaces;

namespace Railway.Engine.Services.Schedulers;

/// <summary>
/// In-memory/testing scheduler selected with <c>RAILWAY_HOOKS_SCHEDULER=fake</c>. It never touches a real
/// scheduler: entries are persisted as a small JSON document per repository under a temp root, so tests
/// can assert exactly what was materialized without launching <c>launchctl</c>, <c>crontab</c> or
/// <c>schtasks</c>.
/// </summary>
/// <remarks>
/// The root is the <see cref="RootEnvironmentVariable"/> environment variable when set, otherwise
/// <c>&lt;temp&gt;/railway-fake-scheduler</c>; the constructor also accepts an explicit override.
/// </remarks>
public sealed class FakeScheduler : SchedulerBase
{
    /// <summary>Environment variable overriding the fake scheduler's root directory.</summary>
    public const string RootEnvironmentVariable = "RAILWAY_HOOKS_FAKE_SCHEDULER_ROOT";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _root;

    /// <summary>Initializes a new instance of the <see cref="FakeScheduler"/> class.</summary>
    /// <param name="paths">Resolves repository slugs.</param>
    /// <param name="rootOverride">Explicit root directory (tests); falls back to the environment variable then a temp directory.</param>
    /// <param name="environment">Environment reader; defaults to the process environment.</param>
    public FakeScheduler(HookPaths paths, string? rootOverride = null, Func<string, string?>? environment = null)
        : base(paths)
    {
        var readEnvironment = environment ?? ReadEnvironmentVariable;
        _root = rootOverride
            ?? readEnvironment(RootEnvironmentVariable)
            ?? Path.Combine(Path.GetTempPath(), "railway-fake-scheduler");
    }

    /// <inheritdoc />
    public override string Name => "fake";

    /// <inheritdoc />
    public override bool UsesWrapperScript => false;

    /// <inheritdoc />
    public override bool IsSupported() => true;

    /// <summary>Gets the root directory the fake scheduler persists repositories under.</summary>
    public string Root => _root;

    /// <inheritdoc />
    public override IReadOnlyList<ScheduleListEntry> List(string repositoryPath)
    {
        var storePath = GetStorePath(repositoryPath);

        return [.. Read(storePath).Select(record => new ScheduleListEntry(record.PipelineFileName, record.Cron, storePath))];
    }

    /// <inheritdoc />
    public override ScheduleInstallOutcome Install(string repositoryPath, ScheduleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var storePath = GetStorePath(repositoryPath);

        if (!TryParse(request.Cron, out _, out var parseError))
        {
            return new ScheduleInstallOutcome(
                request.PipelineFileName,
                request.Cron,
                storePath,
                ScheduleAction.Failed,
                DescribeUnsupported(request.PipelineFileName, request.Cron, parseError!, null));
        }

        var records = Read(storePath);

        if (records.Any(record => IsMatch(record, request.PipelineFileName, request.Cron)))
        {
            return new ScheduleInstallOutcome(request.PipelineFileName, request.Cron, storePath, ScheduleAction.Unchanged);
        }

        records.Add(new FakeScheduleRecord(request.PipelineFileName, request.Cron));
        Write(storePath, records);

        return new ScheduleInstallOutcome(request.PipelineFileName, request.Cron, storePath, ScheduleAction.Created);
    }

    /// <inheritdoc />
    public override ScheduleRemoveOutcome Remove(string repositoryPath, string pipelineFileName, string cronExpression)
    {
        var storePath = GetStorePath(repositoryPath);
        var records = Read(storePath);
        var remaining = records.Where(record => !IsMatch(record, pipelineFileName, cronExpression)).ToList();

        if (remaining.Count == records.Count)
        {
            return new ScheduleRemoveOutcome(pipelineFileName, cronExpression, storePath, ScheduleAction.Unchanged);
        }

        Write(storePath, remaining);
        return new ScheduleRemoveOutcome(pipelineFileName, cronExpression, storePath, ScheduleAction.Removed);
    }

    /// <inheritdoc />
    public override ScheduleStatusEntry Inspect(string repositoryPath, ScheduleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParse(request.Cron, out _, out var parseError))
        {
            return new ScheduleStatusEntry(
                request.PipelineFileName,
                request.Cron,
                ScheduleState.Unsupported,
                null,
                DescribeUnsupported(request.PipelineFileName, request.Cron, parseError!, null));
        }

        var storePath = GetStorePath(repositoryPath);
        var records = Read(storePath);

        if (records.Any(record => IsMatch(record, request.PipelineFileName, request.Cron)))
        {
            return new ScheduleStatusEntry(request.PipelineFileName, request.Cron, ScheduleState.Installed, storePath);
        }

        var drifted = records.Any(record => string.Equals(record.PipelineFileName, request.PipelineFileName, StringComparison.Ordinal));

        return new ScheduleStatusEntry(
            request.PipelineFileName,
            request.Cron,
            drifted ? ScheduleState.Drifted : ScheduleState.Missing,
            null);
    }

    private static bool IsMatch(FakeScheduleRecord record, string pipelineFileName, string cronExpression) =>
        string.Equals(record.PipelineFileName, pipelineFileName, StringComparison.Ordinal)
        && TryNormalize(record.Cron, out var stored)
        && TryNormalize(cronExpression, out var requested)
        && string.Equals(stored, requested, StringComparison.Ordinal);

    private string GetStorePath(string repositoryPath) =>
        Path.Combine(_root, Paths.GetRepositorySlug(repositoryPath) + ".json");

    private static List<FakeScheduleRecord> Read(string storePath)
    {
        if (!File.Exists(storePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<FakeScheduleRecord>>(File.ReadAllText(storePath)) ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Write(string storePath, List<FakeScheduleRecord> records)
    {
        if (records.Count == 0)
        {
            if (File.Exists(storePath))
            {
                File.Delete(storePath);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);

        var ordered = records
            .OrderBy(record => record.PipelineFileName, StringComparer.Ordinal)
            .ThenBy(record => record.Cron, StringComparer.Ordinal)
            .ToList();

        File.WriteAllText(storePath, JsonSerializer.Serialize(ordered, SerializerOptions));
    }

    private sealed record FakeScheduleRecord(string PipelineFileName, string Cron);
}
