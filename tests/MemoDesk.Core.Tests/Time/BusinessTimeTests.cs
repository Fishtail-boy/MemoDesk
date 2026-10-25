using MemoDesk.Core.Time;

namespace MemoDesk.Core.Tests.Time;

public sealed class BusinessTimeTests
{
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

    [Fact]
    public void DateOf_ConvertsByBusinessTimeZone()
    {
        var utc = new DateTimeOffset(2026, 10, 21, 17, 30, 0, TimeSpan.Zero); // 上海 2026-10-22 01:30

        Assert.Equal(new DateOnly(2026, 10, 22), BusinessTime.DateOf(utc, Shanghai));
    }

    [Fact]
    public void Combine_UsesBusinessOffset()
    {
        var result = BusinessTime.Combine(new DateOnly(2026, 10, 22), new TimeOnly(15, 0), Shanghai);

        Assert.Equal(new DateTimeOffset(2026, 10, 22, 7, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void TomorrowAt_UsesNextNaturalDayNot24Hours()
    {
        var lateNight = new DateTimeOffset(2026, 10, 21, 15, 50, 0, TimeSpan.Zero); // 上海 23:50

        var result = BusinessTime.TomorrowAt(lateNight, Shanghai, new TimeOnly(9, 0));

        // 10 小时后，不是 24 小时后
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 1, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void StartOfDateUtc_ReturnsLocalMidnight()
    {
        var result = BusinessTime.StartOfDateUtc(new DateOnly(2026, 10, 21), Shanghai);

        Assert.Equal(new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero), result);
    }
}
