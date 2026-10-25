using MemoDesk.Core.Time;

namespace MemoDesk.Core.Reminders;

public static class SnoozeCalculator
{
    /// <summary>计算稍后提醒的目标时刻（UTC）。</summary>
    public static DateTimeOffset Compute(SnoozeOption option, DateTimeOffset nowUtc, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(timeZone);

        return option.Kind switch
        {
            SnoozeKind.Relative => nowUtc.AddMinutes(option.Minutes),
            SnoozeKind.TomorrowAt => BusinessTime.TomorrowAt(
                nowUtc,
                timeZone,
                option.TomorrowTime ?? new TimeOnly(9, 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(option), option.Kind, "未知的稍后提醒规则。"),
        };
    }
}
