namespace MemoDesk.Core.Reminders;

/// <summary>稍后提醒选项的计算规则。</summary>
public enum SnoozeKind
{
    /// <summary>从用户点击时开始的相对时长。</summary>
    Relative = 0,

    /// <summary>业务时区下一个自然日的固定时刻。</summary>
    TomorrowAt = 1,
}

/// <summary>稍后提醒选项（设计 5.2）。每个选项有稳定标识、显示名称和计算规则。</summary>
public sealed record SnoozeOption(
    string Id,
    string Label,
    SnoozeKind Kind,
    int Minutes,
    TimeOnly? TomorrowTime)
{
    public static IReadOnlyList<SnoozeOption> Defaults { get; } = new[]
    {
        new SnoozeOption("10m", "10 分钟", SnoozeKind.Relative, 10, null),
        new SnoozeOption("30m", "30 分钟", SnoozeKind.Relative, 30, null),
        new SnoozeOption("1h", "1 小时", SnoozeKind.Relative, 60, null),
        new SnoozeOption("tomorrow", "明天 09:00", SnoozeKind.TomorrowAt, 0, new TimeOnly(9, 0)),
    };
}
