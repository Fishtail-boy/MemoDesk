namespace MemoDesk.Core.Domain;

/// <summary>
/// 物理事项主表承载三类业务对象（设计 7.3）。工作记录不使用完成状态，
/// 但共用状态列以便统一检索；界面按类型解释状态。
/// </summary>
public enum ItemStatus
{
    Open = 0,
    Completed = 1,
    Archived = 2,
    Deleted = 3,
}

public enum ItemPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
}

/// <summary>工作发生时间精度（设计 3.2）：精确时刻、日期、日内时段、跨日区间。</summary>
public enum WorkTimePrecision
{
    Exact = 0,
    Date = 1,
    Period = 2,
    Range = 3,
}

/// <summary>
/// 工作发生时间。日期为业务日期；时段/区间保留原始表达；
/// 只精确到日期的记录不伪造分钟。跨日区间用 EndDate 表示结束日期。
/// </summary>
public sealed record WorkTimeInfo(
    DateOnly Date,
    TimeOnly? Start,
    TimeOnly? End,
    string? OriginalText,
    WorkTimePrecision Precision,
    DateOnly? EndDate = null);

/// <summary>正式事项（设计 7.3）。</summary>
public sealed record Item
{
    public required Guid Id { get; init; }

    public Guid? CaptureId { get; init; }

    /// <summary>来源候选项的稳定标识，与 CaptureId 组成唯一约束防重复入库。</summary>
    public Guid? CandidateSourceId { get; init; }

    public required ItemType Type { get; init; }

    public required string Content { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public ItemPriority? Priority { get; init; }

    public required ItemStatus Status { get; init; }

    /// <summary>待办的事项日期（业务日期）；可为空表示未安排。</summary>
    public DateOnly? ItemDate { get; init; }

    /// <summary>待办的截止时间（UTC）。</summary>
    public DateTimeOffset? DueAtUtc { get; init; }

    /// <summary>备忘的展示日期（业务日期）。</summary>
    public DateOnly? MemoDate { get; init; }

    /// <summary>工作记录的发生时间。</summary>
    public WorkTimeInfo? WorkTime { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public DateTimeOffset? ArchivedAtUtc { get; init; }

    public DateTimeOffset? DeletedAtUtc { get; init; }

    public int RowVersion { get; init; } = 1;

    /// <summary>用户是否修改过内容/提醒等；后台 AI 结果不得覆盖人工修改。</summary>
    public bool HumanRevised { get; init; }
}
