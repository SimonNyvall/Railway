using System.Text;

using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Railway.Engine.Services.Interfaces;

namespace Railway.Engine.Services.Schedulers;

/// <summary>
/// Materializes schedule triggers into the user's crontab, inside one marker-delimited block per
/// (repository, pipeline). The block is read with <c>crontab -l</c> and written with <c>crontab -</c>
/// through <see cref="ISystemCommandRunner"/>; lines outside the marker block are never modified.
/// </summary>
/// <remarks>
/// The block markers are <c># railway: &lt;repoSlug&gt;:&lt;pipelineSlug&gt; begin</c> /
/// <c>… end</c> (see <see cref="HookScriptHelper.GetCrontabBlockStart"/>). Each owned line is preceded by
/// provenance comments (<c># pipeline:</c>, <c># cron:</c>) so entries can be listed and drift detected
/// without re-deriving the pipeline file name from its slug. Schedules run in local time (crontab's
/// natural semantics) and their output is appended to <c>.railway/logs/schedule-&lt;slug&gt;.log</c>.
/// </remarks>
public sealed class CrontabScheduler : SchedulerBase
{
    private const string PipelineCommentPrefix = "# pipeline:";
    private const string CronCommentPrefix = "# cron:";

    private readonly ISystemCommandRunner _runner;

    /// <summary>Initializes a new instance of the <see cref="CrontabScheduler"/> class.</summary>
    /// <param name="paths">Resolves repository slugs, wrapper and log paths.</param>
    /// <param name="runner">Runs <c>crontab</c>; faked by tests.</param>
    public CrontabScheduler(HookPaths paths, ISystemCommandRunner runner)
        : base(paths)
    {
        _runner = runner;
    }

    /// <inheritdoc />
    public override string Name => "crontab";

    /// <inheritdoc />
    public override bool UsesWrapperScript => true;

    /// <inheritdoc />
    public override bool IsSupported() => !OperatingSystem.IsWindows();

    /// <inheritdoc />
    public override IReadOnlyList<ScheduleListEntry> List(string repositoryPath)
    {
        var entries = new List<ScheduleListEntry>();

        if (!TryReadCrontab(out var lines, out _))
        {
            return entries;
        }

        var repositorySlug = Paths.GetRepositorySlug(repositoryPath);

        foreach (var block in EnumerateBlocks(lines, repositorySlug))
        {
            foreach (var cron in block.Crons)
            {
                entries.Add(new ScheduleListEntry(block.PipelineFileName, cron, DescribeBlock(repositorySlug, Paths.GetPipelineSlug(block.PipelineFileName))));
            }
        }

        return entries;
    }

    /// <inheritdoc />
    public override ScheduleInstallOutcome Install(string repositoryPath, ScheduleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var repositorySlug = Paths.GetRepositorySlug(repositoryPath);
        var pipelineSlug = Paths.GetPipelineSlug(request.PipelineFileName);
        var entryName = DescribeBlock(repositorySlug, pipelineSlug);

        if (!TryParse(request.Cron, out _, out var parseError))
        {
            return new ScheduleInstallOutcome(
                request.PipelineFileName,
                request.Cron,
                entryName,
                ScheduleAction.Failed,
                DescribeUnsupported(request.PipelineFileName, request.Cron, parseError!, null));
        }

        // The generated line redirects into .railway/logs; create it first or sh fails to start.
        EnsureLogDirectory(repositoryPath);

        if (!TryReadCrontab(out var lines, out var readError))
        {
            return new ScheduleInstallOutcome(
                request.PipelineFileName,
                request.Cron,
                entryName,
                ScheduleAction.Failed,
                $"could not read the crontab for pipeline '{request.PipelineFileName}': {readError}");
        }

        var normalized = NormalizeExpression(request.Cron);
        var block = FindBlock(lines, repositorySlug, pipelineSlug);
        var action = ScheduleAction.Created;

        if (block is not null)
        {
            var crons = block.Crons.ToList();

            if (!crons.Contains(normalized, StringComparer.Ordinal))
            {
                crons.Add(normalized);
            }

            var rebuilt = BuildBlockLines(repositoryPath, request.PipelineFileName, repositorySlug, pipelineSlug, crons);

            if (rebuilt.SequenceEqual(block.Lines, StringComparer.Ordinal))
            {
                return new ScheduleInstallOutcome(request.PipelineFileName, request.Cron, entryName, ScheduleAction.Unchanged);
            }

            lines.RemoveRange(block.Start, block.Lines.Count);
            lines.InsertRange(block.Start, rebuilt);
            action = ScheduleAction.Updated;
        }
        else
        {
            AppendBlock(lines, BuildBlockLines(repositoryPath, request.PipelineFileName, repositorySlug, pipelineSlug, [normalized]));
        }

        var writeError = WriteCrontab(lines);

        return writeError is null
            ? new ScheduleInstallOutcome(request.PipelineFileName, request.Cron, entryName, action)
            : new ScheduleInstallOutcome(request.PipelineFileName, request.Cron, entryName, ScheduleAction.Failed, writeError);
    }

    /// <inheritdoc />
    public override ScheduleRemoveOutcome Remove(string repositoryPath, string pipelineFileName, string cronExpression)
    {
        var repositorySlug = Paths.GetRepositorySlug(repositoryPath);
        var pipelineSlug = Paths.GetPipelineSlug(pipelineFileName);
        var entryName = DescribeBlock(repositorySlug, pipelineSlug);

        if (!TryReadCrontab(out var lines, out var readError))
        {
            return new ScheduleRemoveOutcome(
                pipelineFileName,
                cronExpression,
                entryName,
                ScheduleAction.Failed,
                $"could not read the crontab for pipeline '{pipelineFileName}': {readError}");
        }

        var block = FindBlock(lines, repositorySlug, pipelineSlug);

        if (block is null)
        {
            return new ScheduleRemoveOutcome(pipelineFileName, cronExpression, entryName, ScheduleAction.Unchanged);
        }

        var remaining = TryNormalize(cronExpression, out var normalized)
            ? block.Crons.Where(cron => !string.Equals(cron, normalized, StringComparison.Ordinal)).ToList()
            : block.Crons.ToList();

        if (remaining.Count == block.Crons.Count)
        {
            return new ScheduleRemoveOutcome(pipelineFileName, cronExpression, entryName, ScheduleAction.Unchanged);
        }

        if (remaining.Count == 0)
        {
            var blockEnd = block.Start + block.Lines.Count;
            var wasTail = blockEnd == lines.Count;
            lines.RemoveRange(block.Start, block.Lines.Count);

            // The block was appended with a single blank separator line; drop it too so a
            // remove-then-reinstall round-trip restores the user's crontab byte-for-byte.
            if (wasTail && block.Start > 0 && lines[block.Start - 1].Length == 0)
            {
                lines.RemoveAt(block.Start - 1);
            }
        }
        else
        {
            lines.RemoveRange(block.Start, block.Lines.Count);
            lines.InsertRange(block.Start, BuildBlockLines(repositoryPath, pipelineFileName, repositorySlug, pipelineSlug, remaining));
        }

        var writeError = WriteCrontab(lines);

        return writeError is null
            ? new ScheduleRemoveOutcome(pipelineFileName, cronExpression, entryName, ScheduleAction.Removed)
            : new ScheduleRemoveOutcome(pipelineFileName, cronExpression, entryName, ScheduleAction.Failed, writeError);
    }

    /// <inheritdoc />
    public override ScheduleStatusEntry Inspect(string repositoryPath, ScheduleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var repositorySlug = Paths.GetRepositorySlug(repositoryPath);
        var pipelineSlug = Paths.GetPipelineSlug(request.PipelineFileName);
        var entryName = DescribeBlock(repositorySlug, pipelineSlug);

        if (!TryParse(request.Cron, out _, out var parseError))
        {
            return new ScheduleStatusEntry(
                request.PipelineFileName,
                request.Cron,
                ScheduleState.Unsupported,
                null,
                DescribeUnsupported(request.PipelineFileName, request.Cron, parseError!, null));
        }

        if (!TryReadCrontab(out var lines, out var readError))
        {
            return new ScheduleStatusEntry(request.PipelineFileName, request.Cron, ScheduleState.Missing, null, readError);
        }

        var block = FindBlock(lines, repositorySlug, pipelineSlug);

        if (block is null)
        {
            return new ScheduleStatusEntry(request.PipelineFileName, request.Cron, ScheduleState.Missing, null);
        }

        var normalized = NormalizeExpression(request.Cron);
        var canonical = BuildBlockLines(repositoryPath, request.PipelineFileName, repositorySlug, pipelineSlug, block.Crons);

        var state = block.Crons.Contains(normalized, StringComparer.Ordinal)
            && canonical.SequenceEqual(block.Lines, StringComparer.Ordinal)
                ? ScheduleState.Installed
                : ScheduleState.Drifted;

        return new ScheduleStatusEntry(request.PipelineFileName, request.Cron, state, entryName);
    }

    private string DescribeBlock(string repositorySlug, string pipelineSlug) => $"crontab {repositorySlug}:{pipelineSlug}";

    /// <summary>Rebuilds the canonical block lines for one pipeline.</summary>
    /// <param name="repositoryPath">Repository root (or any path inside it).</param>
    /// <param name="pipelineFileName">Pipeline file name.</param>
    /// <param name="repositorySlug">Slug of the repository.</param>
    /// <param name="pipelineSlug">Slug of the pipeline.</param>
    /// <param name="crons">The canonical cron expressions the block owns, in order.</param>
    private List<string> BuildBlockLines(
        string repositoryPath,
        string pipelineFileName,
        string repositorySlug,
        string pipelineSlug,
        IReadOnlyList<string> crons)
    {
        var wrapperPath = GetScheduleWrapperScriptPath(repositoryPath, pipelineFileName);
        var logPath = GetScheduleLogPath(repositoryPath, pipelineFileName);

        var lines = new List<string>
        {
            HookScriptHelper.GetCrontabBlockStart(repositorySlug, pipelineSlug),
            "# Generated by Railway. Do not edit; run 'railway hooks sync' to refresh.",
            $"{PipelineCommentPrefix} {pipelineFileName}",
        };

        foreach (var cron in crons)
        {
            lines.Add($"{CronCommentPrefix} {cron}");
            lines.Add($"{cron} /bin/sh {HookScriptHelper.QuoteSh(wrapperPath)} {HookScriptHelper.QuoteSh(cron)} >> {HookScriptHelper.QuoteSh(logPath)} 2>&1");
        }

        lines.Add(HookScriptHelper.GetCrontabBlockEnd(repositorySlug, pipelineSlug));
        return lines;
    }

    private static void AppendBlock(List<string> lines, IReadOnlyList<string> block)
    {
        if (lines.Count > 0 && lines[^1].Length > 0)
        {
            lines.Add(string.Empty);
        }

        lines.AddRange(block);
    }

    private static CrontabBlock? FindBlock(IReadOnlyList<string> lines, string repositorySlug, string pipelineSlug)
    {
        var start = HookScriptHelper.GetCrontabBlockStart(repositorySlug, pipelineSlug);
        var end = HookScriptHelper.GetCrontabBlockEnd(repositorySlug, pipelineSlug);
        var startIndex = -1;

        for (var index = 0; index < lines.Count; index++)
        {
            if (!string.Equals(lines[index].TrimEnd(), start, StringComparison.Ordinal))
            {
                continue;
            }

            startIndex = index;
            break;
        }

        if (startIndex < 0)
        {
            return null;
        }

        for (var index = startIndex + 1; index < lines.Count; index++)
        {
            if (!string.Equals(lines[index].TrimEnd(), end, StringComparison.Ordinal))
            {
                continue;
            }

            var blockLines = lines.Skip(startIndex).Take(index - startIndex + 1).ToArray();
            return new CrontabBlock(startIndex, blockLines, ParsePipelineFileName(blockLines), ParseCrons(blockLines));
        }

        // Unterminated block: treat the rest of the crontab as owned so sync can repair it.
        var tail = lines.Skip(startIndex).ToArray();
        return new CrontabBlock(startIndex, tail, ParsePipelineFileName(tail), ParseCrons(tail));
    }

    private static IEnumerable<CrontabBlock> EnumerateBlocks(IReadOnlyList<string> lines, string repositorySlug)
    {
        var prefix = $"{HookScriptHelper.CrontabMarkerPrefix} {repositorySlug}:";

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index].TrimEnd();

            if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith(" begin", StringComparison.Ordinal))
            {
                continue;
            }

            var pipelineSlug = line[prefix.Length..^" begin".Length].Trim();

            if (FindBlock(lines, repositorySlug, pipelineSlug) is { } block && block.Start == index)
            {
                yield return block;
            }
        }
    }

    private static string ParsePipelineFileName(IReadOnlyList<string> blockLines)
    {
        foreach (var line in blockLines)
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith(PipelineCommentPrefix, StringComparison.Ordinal))
            {
                return trimmed[PipelineCommentPrefix.Length..].Trim();
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> ParseCrons(IReadOnlyList<string> blockLines)
    {
        var crons = new List<string>();

        foreach (var line in blockLines)
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith(CronCommentPrefix, StringComparison.Ordinal))
            {
                crons.Add(trimmed[CronCommentPrefix.Length..].Trim());
            }
        }

        return crons;
    }

    private bool TryReadCrontab(out List<string> lines, out string? error)
    {
        lines = [];
        error = null;

        var result = _runner.Run("crontab", ["-l"]);

        if (result.ExitCode == SystemCommandResult.ExecutableNotFoundExitCode)
        {
            error = "crontab is not available on this machine";
            return false;
        }

        // Exit code 1 with no output is how both macOS and Linux report "no crontab for <user>".
        if (result.ExitCode != 0)
        {
            if (string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return true;
            }

            error = FirstLine(result.StandardError);
            return false;
        }

        var text = result.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        lines = text.Length == 0 ? [] : [.. text.Split('\n')];
        return true;
    }

    private string? WriteCrontab(IReadOnlyList<string> lines)
    {
        var text = lines.Count == 0 ? string.Empty : string.Join('\n', lines) + "\n";
        var result = _runner.Run("crontab", ["-"], stdin: text);

        if (result.ExitCode == SystemCommandResult.ExecutableNotFoundExitCode)
        {
            return "crontab is not available on this machine";
        }

        return result.ExitCode == 0
            ? null
            : $"crontab - exited {result.ExitCode}: {FirstLine(result.StandardError)}";
    }

    private sealed record CrontabBlock(int Start, IReadOnlyList<string> Lines, string PipelineFileName, IReadOnlyList<string> Crons);
}
