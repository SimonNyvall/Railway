namespace Railway.Engine.Helpers;

/// <summary>
/// Thrown when a cron expression cannot be parsed or uses an unsupported construct.
/// </summary>
public sealed class CronExpressionException : Exception
{
    /// <summary>The expression that failed to parse, when available.</summary>
    public string? Expression { get; }

    /// <summary>The offending field name (e.g. <c>Minutes</c>), when the failure is field-specific.</summary>
    public string? Field { get; }

    public CronExpressionException(string message, string? expression = null, string? field = null)
        : base(BuildMessage(message, expression, field))
    {
        Expression = expression;
        Field = field;
    }

    private static string BuildMessage(string message, string? expression, string? field)
    {
        if (field is not null)
        {
            message += $" Field: '{field}'.";
        }

        if (!string.IsNullOrEmpty(expression))
        {
            message += $" Expression: \"{expression}\".";
        }

        return message;
    }
}

/// <summary>
/// Minimal 5-field cron parser: <c>Minutes Hours DaysOfMonth Months DaysOfWeek</c>.
/// Supports <c>*</c>, single numbers, ranges <c>a-b</c>, steps <c>a-b/s</c>, <c>*/s</c> and <c>n/s</c>,
/// comma lists of the foregoing, and the aliases <c>@hourly</c>, <c>@daily</c>, <c>@weekly</c>, <c>@monthly</c>.
/// Fails loudly on <c>@reboot</c>, 6/7-field (seconds/year) expressions and the advanced tokens <c>L</c>, <c>W</c>, <c>#</c>.
/// Day-of-week accepts 0-7 where both 0 and 7 mean Sunday.
/// </summary>
public static class CronExpressionHelper
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@hourly"] = "0 * * * *",
        ["@daily"] = "0 0 * * *",
        ["@weekly"] = "0 0 * * 0",
        ["@monthly"] = "0 0 1 * *",
    };

    private static readonly char[] AdvancedTokens = ['L', 'W', '#', '?'];

    /// <summary>Parses a 5-field cron expression (or an alias) into a <see cref="CronSchedule"/>.</summary>
    /// <exception cref="CronExpressionException">The expression is invalid or uses unsupported constructs.</exception>
    public static CronSchedule Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new CronExpressionException("Cron expression is empty.");
        }

        var trimmed = expression.Trim();

        if (trimmed.Equals("@reboot", StringComparison.OrdinalIgnoreCase))
        {
            throw new CronExpressionException(
                "'@reboot' is not supported: it has no time-based schedule.",
                trimmed);
        }

        if (trimmed.StartsWith('@'))
        {
            if (!Aliases.TryGetValue(trimmed, out var expansion))
            {
                throw new CronExpressionException(
                    $"Unknown alias '{trimmed}'. Supported aliases: @hourly, @daily, @weekly, @monthly.",
                    trimmed);
            }

            trimmed = expansion;
        }

        var fields = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (fields.Length is 6 or 7)
        {
            throw new CronExpressionException(
                $"Unsupported field count {fields.Length}: expected 5 fields " +
                "(6/7-field expressions with seconds/year are not supported).",
                trimmed);
        }

        if (fields.Length != 5)
        {
            throw new CronExpressionException(
                $"Expected 5 fields (Minutes Hours DaysOfMonth Months DaysOfWeek) but got {fields.Length}.",
                trimmed);
        }

        RejectAdvancedTokens(fields, trimmed);

        return new CronSchedule(
            ParseField(fields[0], "Minutes", 0, 59, trimmed),
            ParseField(fields[1], "Hours", 0, 23, trimmed),
            ParseField(fields[2], "DaysOfMonth", 1, 31, trimmed),
            ParseField(fields[3], "Months", 1, 12, trimmed),
            ParseField(fields[4], "DaysOfWeek", 0, 7, trimmed, normalizeSevenAsSunday: true));
    }

    private static void RejectAdvancedTokens(string[] fields, string expression)
    {
        var fieldNames = FieldNames;
        for (var i = 0; i < fields.Length; i++)
        {
            foreach (var c in fields[i])
            {
                if (Array.IndexOf(AdvancedTokens, char.ToUpperInvariant(c)) >= 0)
                {
                    throw new CronExpressionException(
                        $"Advanced cron token '{c}' is not supported.",
                        expression,
                        fieldNames[i]);
                }
            }
        }
    }

    private static readonly string[] FieldNames = ["Minutes", "Hours", "DaysOfMonth", "Months", "DaysOfWeek"];

    private static IReadOnlyList<int> ParseField(
        string token,
        string fieldName,
        int min,
        int max,
        string expression,
        bool normalizeSevenAsSunday = false)
    {
        var values = new SortedSet<int>();

        foreach (var part in token.Split(','))
        {
            ParseListPart(part, fieldName, min, max, values, expression, normalizeSevenAsSunday);
        }

        if (values.Count == 0)
        {
            throw new CronExpressionException("Field contains no values.", expression, fieldName);
        }

        return values.ToArray();
    }

    private static void ParseListPart(
        string part,
        string fieldName,
        int min,
        int max,
        SortedSet<int> values,
        string expression,
        bool normalizeSevenAsSunday)
    {
        if (part.Length == 0)
        {
            throw new CronExpressionException("List element is empty (stray comma?).", expression, fieldName);
        }

        var valuePart = part;
        var step = 1;

        var slash = part.IndexOf('/');
        if (slash >= 0)
        {
            valuePart = part[..slash];
            var stepText = part[(slash + 1)..];
            if (!int.TryParse(stepText, out step) || step < 1)
            {
                throw new CronExpressionException(
                    $"Step '{stepText}' in '{part}' must be a positive integer.",
                    expression,
                    fieldName);
            }
        }

        int from, to;
        if (valuePart == "*")
        {
            from = min;
            to = max;
        }
        else
        {
            var dash = valuePart.IndexOf('-');
            if (dash > 0)
            {
                from = ParseNumber(valuePart[..dash], fieldName, min, max, expression, normalizeSevenAsSunday);
                to = ParseNumber(valuePart[(dash + 1)..], fieldName, min, max, expression, normalizeSevenAsSunday);
                if (from > to)
                {
                    throw new CronExpressionException(
                        $"Range '{valuePart}' is reversed; wrapping ranges are not supported.",
                        expression,
                        fieldName);
                }
            }
            else
            {
                from = ParseNumber(valuePart, fieldName, min, max, expression, normalizeSevenAsSunday);

                // 'n/s' is shorthand for 'n-max/s'.
                to = slash >= 0 ? max : from;
            }
        }

        for (var v = from; v <= to; v += step)
        {
            values.Add(normalizeSevenAsSunday && v == 7 ? 0 : v);
        }
    }

    private static int ParseNumber(
        string text,
        string fieldName,
        int min,
        int max,
        string expression,
        bool normalizeSevenAsSunday)
    {
        var lowerBound = normalizeSevenAsSunday ? 0 : min;

        if (!int.TryParse(text, out var value) || value < lowerBound || value > max)
        {
            throw new CronExpressionException(
                $"Value '{text}' is not an integer in range {lowerBound}-{max}.",
                expression,
                fieldName);
        }

        return value;
    }
}

/// <summary>A parsed cron schedule with sorted, distinct field sets.</summary>
public sealed class CronSchedule
{
    internal static readonly IReadOnlyList<int> FullDaysOfMonth = Enumerable.Range(1, 31).ToArray();
    internal static readonly IReadOnlyList<int> FullDaysOfWeek = Enumerable.Range(0, 7).ToArray();

    internal CronSchedule(
        IReadOnlyList<int> minutes,
        IReadOnlyList<int> hours,
        IReadOnlyList<int> daysOfMonth,
        IReadOnlyList<int> months,
        IReadOnlyList<int> daysOfWeek)
    {
        Minutes = minutes;
        Hours = hours;
        DaysOfMonth = daysOfMonth;
        Months = months;
        DaysOfWeek = daysOfWeek;
        IsDayOfMonthRestricted = !daysOfMonth.SequenceEqual(FullDaysOfMonth);
        IsDayOfWeekRestricted = !daysOfWeek.SequenceEqual(FullDaysOfWeek);
    }

    /// <summary>Sorted distinct minute values (0-59).</summary>
    public IReadOnlyList<int> Minutes { get; }

    /// <summary>Sorted distinct hour values (0-23).</summary>
    public IReadOnlyList<int> Hours { get; }

    /// <summary>Sorted distinct day-of-month values (1-31).</summary>
    public IReadOnlyList<int> DaysOfMonth { get; }

    /// <summary>Sorted distinct month values (1-12).</summary>
    public IReadOnlyList<int> Months { get; }

    /// <summary>Sorted distinct day-of-week values (0-6, Sunday = 0). 7 is normalized to 0.</summary>
    public IReadOnlyList<int> DaysOfWeek { get; }

    /// <summary>True when the day-of-month field is restricted (not the full 1-31 set).</summary>
    public bool IsDayOfMonthRestricted { get; }

    /// <summary>True when the day-of-week field is restricted (not the full 0-6 set).</summary>
    public bool IsDayOfWeekRestricted { get; }

    /// <summary>
    /// Returns the first occurrence strictly after <paramref name="from"/>, as wall-clock time in
    /// <paramref name="zone"/> (a <see cref="DateTimeKind.Utc"/> input is converted first). Invalid local
    /// times created by DST spring-forward gaps are skipped, so a schedule that lands inside a gap runs
    /// after the gap; ambiguous fall-back times resolve to the first (DST) pass.
    /// </summary>
    /// <exception cref="CronExpressionException">No occurrence exists within 5 years of <paramref name="from"/>.</exception>
    public DateTime GetNextOccurrence(DateTime from, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var start = from.Kind == DateTimeKind.Utc
            ? TimeZoneInfo.ConvertTimeFromUtc(from, zone)
            : from;

        // First whole minute strictly after 'from'; treat as wall-clock in 'zone'.
        var candidate = DateTime.SpecifyKind(
            new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0).AddMinutes(1),
            DateTimeKind.Unspecified);

        var limit = candidate.AddYears(5);

        while (candidate <= limit)
        {
            if (zone.IsInvalidTime(candidate))
            {
                // DST spring-forward gap; gaps are short, advance minute-wise to the other side.
                candidate = candidate.AddMinutes(1);
                continue;
            }

            if (!Months.Contains(candidate.Month))
            {
                candidate = new DateTime(candidate.Year, candidate.Month, 1).AddMonths(1);
                continue;
            }

            if (!DayMatches(candidate))
            {
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!Hours.Contains(candidate.Hour))
            {
                candidate = candidate.Date.AddHours(candidate.Hour + 1);
                continue;
            }

            if (!Minutes.Contains(candidate.Minute))
            {
                candidate = candidate.Date.AddHours(candidate.Hour).AddMinutes(candidate.Minute + 1);
                continue;
            }

            return candidate;
        }

        throw new CronExpressionException(
            $"Schedule has no occurrence within 5 years after {from:u}.");
    }

    /// <summary>
    /// Cron day semantics: day-of-month OR day-of-week when both fields are restricted, otherwise the
    /// restricted field (if any) alone.
    /// </summary>
    private bool DayMatches(DateTime date)
    {
        var domHit = DaysOfMonth.Contains(date.Day);
        var dowHit = DaysOfWeek.Contains((int)date.DayOfWeek);

        if (IsDayOfMonthRestricted && IsDayOfWeekRestricted)
        {
            return domHit || dowHit;
        }

        if (IsDayOfMonthRestricted)
        {
            return domHit;
        }

        if (IsDayOfWeekRestricted)
        {
            return dowHit;
        }

        return true;
    }

    /// <summary>
    /// Expands the schedule into launchd <c>StartCalendarInterval</c> entries: keys within one entry are
    /// ANDed, entries are ORed. Cron's <c>dom OR dow</c> rule therefore becomes
    /// <c>|dom| + |dow|</c> entries (one per day-of-month value with a full day-of-week set, plus one
    /// per day-of-week value with a full day-of-month set). Wildcard fields keep their full value list;
    /// restricted day-of-month/day-of-week fields contribute one entry per value.
    /// </summary>
    public IReadOnlyList<LaunchdCalendarEntry> ExpandForLaunchd()
    {
        var entries = new List<LaunchdCalendarEntry>();

        if (IsDayOfMonthRestricted)
        {
            foreach (var dom in DaysOfMonth)
            {
                entries.Add(new LaunchdCalendarEntry(
                    Minutes, Hours, [dom], Months, FullDaysOfWeek));
            }
        }

        if (IsDayOfWeekRestricted)
        {
            foreach (var dow in DaysOfWeek)
            {
                entries.Add(new LaunchdCalendarEntry(
                    Minutes, Hours, FullDaysOfMonth, Months, [dow]));
            }
        }

        if (entries.Count == 0)
        {
            entries.Add(new LaunchdCalendarEntry(
                Minutes, Hours, FullDaysOfMonth, Months, FullDaysOfWeek));
        }

        return entries;
    }
}

/// <summary>One launchd <c>StartCalendarInterval</c> dict: keys are ANDed within the entry.</summary>
public sealed record LaunchdCalendarEntry(
    IReadOnlyList<int> Minutes,
    IReadOnlyList<int> Hours,
    IReadOnlyList<int> DaysOfMonth,
    IReadOnlyList<int> Months,
    IReadOnlyList<int> DaysOfWeek);
