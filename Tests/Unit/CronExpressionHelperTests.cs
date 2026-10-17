using Railway.Engine.Helpers;

using Xunit;

namespace Tests.Unit;

public class CronExpressionHelperTests
{
    // ---- Valid parses -------------------------------------------------------

    [Fact]
    public void Parse_AllWildcards_YieldsFullSets()
    {
        var schedule = CronExpressionHelper.Parse("* * * * *");

        Assert.Equal(Enumerable.Range(0, 60), schedule.Minutes);
        Assert.Equal(Enumerable.Range(0, 24), schedule.Hours);
        Assert.Equal(Enumerable.Range(1, 31), schedule.DaysOfMonth);
        Assert.Equal(Enumerable.Range(1, 12), schedule.Months);
        Assert.Equal(Enumerable.Range(0, 7), schedule.DaysOfWeek);
        Assert.False(schedule.IsDayOfMonthRestricted);
        Assert.False(schedule.IsDayOfWeekRestricted);
    }

    [Fact]
    public void Parse_SingleNumbers_ParsesEachField()
    {
        var schedule = CronExpressionHelper.Parse("30 14 5 9 *");

        Assert.Equal([30], schedule.Minutes);
        Assert.Equal([14], schedule.Hours);
        Assert.Equal([5], schedule.DaysOfMonth);
        Assert.Equal([9], schedule.Months);
    }

    [Fact]
    public void Parse_CommaList_YieldsSortedDistinctValues()
    {
        var schedule = CronExpressionHelper.Parse("30,0,30,15 * * * *");

        Assert.Equal([0, 15, 30], schedule.Minutes);
    }

    [Fact]
    public void Parse_Range_YieldsInclusiveSpan()
    {
        var schedule = CronExpressionHelper.Parse("1-5 9-17 * * *");

        Assert.Equal([1, 2, 3, 4, 5], schedule.Minutes);
        Assert.Equal([9, 10, 11, 12, 13, 14, 15, 16, 17], schedule.Hours);
    }

    [Fact]
    public void Parse_StepWildcard_YieldsSteppedValues()
    {
        var schedule = CronExpressionHelper.Parse("*/10 * * * *");

        Assert.Equal([0, 10, 20, 30, 40, 50], schedule.Minutes);
    }

    [Fact]
    public void Parse_SteppedRange_YieldsSteppedValues()
    {
        var schedule = CronExpressionHelper.Parse("5-25/5 * * * *");

        Assert.Equal([5, 10, 15, 20, 25], schedule.Minutes);
    }

    [Fact]
    public void Parse_StepOnSingleValue_SpansToFieldMax()
    {
        var schedule = CronExpressionHelper.Parse("0/15 * * * *");

        Assert.Equal([0, 15, 30, 45], schedule.Minutes);
    }

    [Fact]
    public void Parse_DayOfWeekSeven_NormalizedToZero()
    {
        var schedule = CronExpressionHelper.Parse("0 0 * * 7");

        Assert.Equal([0], schedule.DaysOfWeek);
        Assert.True(schedule.IsDayOfWeekRestricted);
    }

    [Theory]
    [InlineData("@hourly", "0 * * * *")]
    [InlineData("@daily", "0 0 * * *")]
    [InlineData("@weekly", "0 0 * * 0")]
    [InlineData("@monthly", "0 0 1 * *")]
    public void Parse_Aliases_MatchStandardExpressions(string alias, string standard)
    {
        var aliasSchedule = CronExpressionHelper.Parse(alias);
        var standardSchedule = CronExpressionHelper.Parse(standard);

        Assert.Equal(standardSchedule.Minutes, aliasSchedule.Minutes);
        Assert.Equal(standardSchedule.Hours, aliasSchedule.Hours);
        Assert.Equal(standardSchedule.DaysOfMonth, aliasSchedule.DaysOfMonth);
        Assert.Equal(standardSchedule.Months, aliasSchedule.Months);
        Assert.Equal(standardSchedule.DaysOfWeek, aliasSchedule.DaysOfWeek);
    }

    // ---- Invalid parses -----------------------------------------------------

    [Fact]
    public void Parse_Reboot_Throws()
    {
        var ex = Assert.Throws<CronExpressionException>(() => CronExpressionHelper.Parse("@reboot"));

        Assert.Contains("@reboot", ex.Message);
    }

    [Fact]
    public void Parse_SixFields_Throws()
    {
        var ex = Assert.Throws<CronExpressionException>(() => CronExpressionHelper.Parse("0 0 * * * *"));

        Assert.Contains("6", ex.Message);
    }

    [Fact]
    public void Parse_SevenFields_Throws()
    {
        var ex = Assert.Throws<CronExpressionException>(() => CronExpressionHelper.Parse("* * * * * * *"));

        Assert.Contains("7", ex.Message);
    }

    [Fact]
    public void Parse_TooFewFields_Throws()
    {
        Assert.Throws<CronExpressionException>(() => CronExpressionHelper.Parse("0 0 * *"));
    }

    [Theory]
    [InlineData("L * * * *", "Minutes")]
    [InlineData("* L * * *", "Hours")]
    [InlineData("* * 1W * *", "DaysOfMonth")]
    [InlineData("* * * * 5#3", "DaysOfWeek")]
    public void Parse_AdvancedTokens_ThrowsNamingTheField(string expression, string expectedField)
    {
        var ex = Assert.Throws<CronExpressionException>(() => CronExpressionHelper.Parse(expression));

        Assert.Contains(expectedField, ex.Message);
        Assert.Equal(expectedField, ex.Field);
    }

    [Theory]
    [InlineData("60 * * * *", "Minutes")]
    [InlineData("0 24 * * *", "Hours")]
    [InlineData("0 0 32 * *", "DaysOfMonth")]
    [InlineData("0 0 0 * *", "DaysOfMonth")]
    [InlineData("0 0 * 13 *", "Months")]
    [InlineData("0 0 * * 8", "DaysOfWeek")]
    public void Parse_OutOfRangeValue_ThrowsNamingTheField(string expression, string expectedField)
    {
        var ex = Assert.Throws<CronExpressionException>(() => CronExpressionHelper.Parse(expression));

        Assert.Equal(expectedField, ex.Field);
        Assert.Contains(expectedField, ex.Message);
    }

    [Fact]
    public void Parse_ReversedRange_Throws()
    {
        var ex = Assert.Throws<CronExpressionException>(() => CronExpressionHelper.Parse("10-5 * * * *"));

        Assert.Contains("Minutes", ex.Message);
    }

    [Fact]
    public void Parse_ZeroStep_Throws()
    {
        Assert.Throws<CronExpressionException>(() => CronExpressionHelper.Parse("*/0 * * * *"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Empty_Throws(string expression)
    {
        Assert.Throws<CronExpressionException>(() => CronExpressionHelper.Parse(expression));
    }

    [Fact]
    public void Parse_ErrorMessages_ListTheOffendingField()
    {
        var ex = Assert.Throws<CronExpressionException>(() => CronExpressionHelper.Parse("0 99 * * *"));

        Assert.Contains("Hours", ex.Message);
        Assert.Contains("99", ex.Message);
        Assert.Contains("0 99 * * *", ex.Message);
    }

    // ---- GetNextOccurrence --------------------------------------------------

    [Fact]
    public void GetNextOccurrence_DailySchedule_ReturnsNextDay()
    {
        var schedule = CronExpressionHelper.Parse("0 3 * * *");

        var next = schedule.GetNextOccurrence(new DateTime(2026, 1, 15, 10, 0, 0), TimeZoneInfo.Utc);

        Assert.Equal(new DateTime(2026, 1, 16, 3, 0, 0), next);
    }

    [Fact]
    public void GetNextOccurrence_FromExactOccurrence_IsStrictlyAfter()
    {
        var schedule = CronExpressionHelper.Parse("0 3 * * *");

        var next = schedule.GetNextOccurrence(new DateTime(2026, 1, 15, 3, 0, 0), TimeZoneInfo.Utc);

        Assert.Equal(new DateTime(2026, 1, 16, 3, 0, 0), next);
    }

    [Fact]
    public void GetNextOccurrence_MonthRollover_ReturnsFirstOfNextMonth()
    {
        var schedule = CronExpressionHelper.Parse("0 0 1 * *");

        var next = schedule.GetNextOccurrence(new DateTime(2026, 1, 15, 12, 0, 0), TimeZoneInfo.Utc);

        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0), next);
    }

    [Fact]
    public void GetNextOccurrence_DowRestricted_ReturnsNextMatchingWeekday()
    {
        // 2026-01-14 is a Wednesday; schedule runs on Mondays.
        var schedule = CronExpressionHelper.Parse("0 3 * * 1");

        var next = schedule.GetNextOccurrence(new DateTime(2026, 1, 14, 8, 0, 0), TimeZoneInfo.Utc);

        Assert.Equal(new DateTime(2026, 1, 19, 3, 0, 0), next);
    }

    [Fact]
    public void GetNextOccurrence_DowAlias7_BehavesAsSunday()
    {
        // 2026-01-14 is a Wednesday; dow 7 == Sunday == 0.
        var schedule = CronExpressionHelper.Parse("0 3 * * 7");

        var next = schedule.GetNextOccurrence(new DateTime(2026, 1, 14, 8, 0, 0), TimeZoneInfo.Utc);

        Assert.Equal(new DateTime(2026, 1, 18, 3, 0, 0), next);
    }

    [Fact]
    public void GetNextOccurrence_DomOrDow_UsesOrSemantics()
    {
        // 2026-01-14 (Wednesday): dom=13 already passed, so the next hit is dow=5 (Friday) 2026-01-16.
        var schedule = CronExpressionHelper.Parse("0 3 13 * 5");

        var next = schedule.GetNextOccurrence(new DateTime(2026, 1, 14, 8, 0, 0), TimeZoneInfo.Utc);

        Assert.Equal(new DateTime(2026, 1, 16, 3, 0, 0), next);
    }

    [Fact]
    public void GetNextOccurrence_LeapDay_ReturnsFeb29()
    {
        var schedule = CronExpressionHelper.Parse("0 0 29 2 *");

        var next = schedule.GetNextOccurrence(new DateTime(2026, 3, 1, 0, 0, 0), TimeZoneInfo.Utc);

        Assert.Equal(new DateTime(2028, 2, 29, 0, 0, 0), next);
    }

    [Fact]
    public void GetNextOccurrence_SpringForwardGap_SkipsInvalidLocalTime()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        // US DST starts 2026-03-08; 02:30 that day does not exist, so the run slips to 2026-03-09.
        var schedule = CronExpressionHelper.Parse("30 2 * * *");

        var next = schedule.GetNextOccurrence(new DateTime(2026, 3, 7, 12, 0, 0), zone);

        Assert.Equal(new DateTime(2026, 3, 9, 2, 30, 0), next);
    }

    [Fact]
    public void GetNextOccurrence_FallBackAmbiguousTime_ReturnsFirstPass()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        // US DST ends 2026-11-01; 01:30 occurs twice (EDT then EST); we return the wall-clock time.
        var schedule = CronExpressionHelper.Parse("30 1 * * *");

        var next = schedule.GetNextOccurrence(new DateTime(2026, 10, 31, 12, 0, 0), zone);

        Assert.Equal(new DateTime(2026, 11, 1, 1, 30, 0), next);
    }

    // ---- ExpandForLaunchd ---------------------------------------------------

    [Fact]
    public void ExpandForLaunchd_PureAnd_SingleEntryWithFullDaySets()
    {
        var entries = CronExpressionHelper.Parse("0 3 * * *").ExpandForLaunchd();

        var entry = Assert.Single(entries);
        Assert.Equal([0], entry.Minutes);
        Assert.Equal([3], entry.Hours);
        Assert.Equal(Enumerable.Range(1, 31), entry.DaysOfMonth);
        Assert.Equal(Enumerable.Range(1, 12), entry.Months);
        Assert.Equal(Enumerable.Range(0, 7), entry.DaysOfWeek);
    }

    [Fact]
    public void ExpandForLaunchd_DowRestrictedList_OneEntryPerDowValue()
    {
        var entries = CronExpressionHelper.Parse("0 3 * * 1,5").ExpandForLaunchd();

        Assert.Equal(2, entries.Count);

        Assert.Equal([0], entries[0].Minutes);
        Assert.Equal([3], entries[0].Hours);
        Assert.Equal(Enumerable.Range(1, 31), entries[0].DaysOfMonth);
        Assert.Equal(Enumerable.Range(1, 12), entries[0].Months);
        Assert.Equal([1], entries[0].DaysOfWeek);

        Assert.Equal([0], entries[1].Minutes);
        Assert.Equal([3], entries[1].Hours);
        Assert.Equal(Enumerable.Range(1, 31), entries[1].DaysOfMonth);
        Assert.Equal(Enumerable.Range(1, 12), entries[1].Months);
        Assert.Equal([5], entries[1].DaysOfWeek);
    }

    [Fact]
    public void ExpandForLaunchd_DomAndDowBothRestricted_OrsAsDomEntriesPlusDowEntries()
    {
        // cron: dom OR dow -> |dom| + |dow| = 2 + 1 = 3 entries.
        var entries = CronExpressionHelper.Parse("0 3 1,15 * 1").ExpandForLaunchd();

        Assert.Equal(3, entries.Count);

        // dom-side entries: restricted dom, full dow.
        Assert.Equal([1], entries[0].DaysOfMonth);
        Assert.Equal(Enumerable.Range(0, 7), entries[0].DaysOfWeek);
        Assert.Equal([15], entries[1].DaysOfMonth);
        Assert.Equal(Enumerable.Range(0, 7), entries[1].DaysOfWeek);

        // dow-side entry: full dom, restricted dow.
        Assert.Equal(Enumerable.Range(1, 31), entries[2].DaysOfMonth);
        Assert.Equal([1], entries[2].DaysOfWeek);

        foreach (var entry in entries)
        {
            Assert.Equal([0], entry.Minutes);
            Assert.Equal([3], entry.Hours);
            Assert.Equal(Enumerable.Range(1, 12), entry.Months);
        }
    }

    [Fact]
    public void ExpandForLaunchd_DomRestrictedAnd_DowWildcard_OrsDomAcrossEntries()
    {
        var entries = CronExpressionHelper.Parse("0 3 1,15 * *").ExpandForLaunchd();

        Assert.Equal(2, entries.Count);
        Assert.Equal([1], entries[0].DaysOfMonth);
        Assert.Equal(Enumerable.Range(0, 7), entries[0].DaysOfWeek);
        Assert.Equal([15], entries[1].DaysOfMonth);
        Assert.Equal(Enumerable.Range(0, 7), entries[1].DaysOfWeek);
    }
}
