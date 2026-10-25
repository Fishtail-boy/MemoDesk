namespace MemoDesk.Core.Timeline;

/// <summary>时间线事件来源类型（设计 7.6）。</summary>
public enum TimelineEventType
{
    /// <summary>工作记录本身（以其发生时间参与时间线）。</summary>
    WorkOccurred = 0,

    /// <summary>待办/备忘的完成事件，使用实际完成时间。</summary>
    ItemCompleted = 1,

    /// <summary>一条备忘被录入的事件，使用原文捕获时间。</summary>
    MemoCaptured = 2,
}

/// <summary>时间线事件；完成事件保留发生时的内容快照。</summary>
public sealed record TimelineEvent(
    Guid Id,
    Guid? ItemId,
    TimelineEventType EventType,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    string ContentSnapshot);
