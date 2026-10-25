using MemoDesk.Core.Drafting;
using MemoDesk.Core.Domain;

namespace MemoDesk.Core.Tests.Drafting;

public sealed class WorkTimeRulesTests
{
    private static readonly DateOnly Day = new(2026, 10, 23);

    [Fact]
    public void UnchangedRange_KeepsEndDate()
    {
        var original = new WorkTimeInfo(
            Day, new TimeOnly(9, 0), new TimeOnly(17, 0), "multi-day", WorkTimePrecision.Range, Day.AddDays(2));

        var result = WorkTimeRules.Build(
            original, Day, "09:00", "17:00", Day.AddDays(2), Day);

        Assert.Null(result.Error);
        Assert.Equal(original, result.WorkTime);
    }

    [Fact]
    public void UnchangedPeriod_KeepsTextAndPrecision()
    {
        var original = new WorkTimeInfo(Day, null, null, "上午", WorkTimePrecision.Period);

        var result = WorkTimeRules.Build(original, Day, string.Empty, string.Empty, null, Day);

        Assert.Null(result.Error);
        Assert.Equal(WorkTimePrecision.Period, result.WorkTime.Precision);
        Assert.Equal("上午", result.WorkTime.OriginalText);
    }

    [Fact]
    public void DateOnlyChange_ShiftsRangeEndDate()
    {
        var original = new WorkTimeInfo(
            Day, new TimeOnly(9, 0), new TimeOnly(17, 0), null, WorkTimePrecision.Range, Day.AddDays(2));

        var result = WorkTimeRules.Build(
            original, Day.AddDays(1), "09:00", "17:00", Day.AddDays(2), Day);

        Assert.Null(result.Error);
        Assert.Equal(Day.AddDays(1), result.WorkTime.Date);
        Assert.Equal(Day.AddDays(3), result.WorkTime.EndDate);
        Assert.Equal(WorkTimePrecision.Range, result.WorkTime.Precision);
    }

    [Fact]
    public void BackwardsExplicitEndDate_IsError()
    {
        var original = new WorkTimeInfo(
            Day, new TimeOnly(9, 0), new TimeOnly(10, 0), null, WorkTimePrecision.Range);

        var result = WorkTimeRules.Build(
            original, Day, "09:00", "10:00", Day.AddDays(-1), Day);

        Assert.NotNull(result.Error);
        Assert.Contains("结束时间", result.Error);
    }

    [Fact]
    public void ExactToOvernight_InfersNextDay()
    {
        var original = new WorkTimeInfo(Day, new TimeOnly(9, 0), null, null, WorkTimePrecision.Exact);

        var result = WorkTimeRules.Build(original, Day, "22:00", "02:00", null, Day);

        Assert.Null(result.Error);
        Assert.Equal(WorkTimePrecision.Range, result.WorkTime.Precision);
        Assert.Equal(Day.AddDays(1), result.WorkTime.EndDate);
    }
}
