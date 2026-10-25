using MemoDesk.Core.Reminders;

namespace MemoDesk.Core.Tests.Reminders;

public sealed class SnoozeCalculatorTests
{
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 6, 30, 0, TimeSpan.Zero);

    [Fact]
    public void RelativeOption_AddsMinutesFromClickTime()
    {
        var option = new SnoozeOption("10m", "10 分钟", SnoozeKind.Relative, 10, null);

        Assert.Equal(Now.AddMinutes(10), SnoozeCalculator.Compute(option, Now, Shanghai));
    }

    [Fact]
    public void TomorrowOption_UsesNextBusinessDayAtConfiguredTime()
    {
        var option = new SnoozeOption("tomorrow", "明天 09:00", SnoozeKind.TomorrowAt, 0, new TimeOnly(9, 0));

        var result = SnoozeCalculator.Compute(option, Now, Shanghai);

        Assert.Equal(new DateTimeOffset(2026, 10, 22, 1, 0, 0, TimeSpan.Zero), result);
    }
}
