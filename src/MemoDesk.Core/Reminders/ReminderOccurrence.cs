namespace MemoDesk.Core.Reminders;

/// <summary>提醒计划（设计 7.4）：每个事项同一时刻最多一个有效计划。</summary>
public sealed record ReminderPlan(
    Guid Id,
    Guid ItemId,
    DateTimeOffset ReminderAtUtc,
    bool Enabled,
    int PlanVersion,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? DeliveryOwner = null,
    string? ScheduledTag = null,
    DateTimeOffset? ScheduledAtUtc = null,
    string? ScheduleState = null,
    int? SchedulePlanVersion = null,
    string? ScheduleError = null,
    DateTimeOffset? ScheduleUpdatedAtUtc = null,
    string Source = "Legacy",
    long? RelativeSeconds = null,
    DateTimeOffset? CapturedAtUtc = null,
    string? SourceTimeText = null);

/// <summary>提醒实例状态（设计 5.5）。</summary>
public enum ReminderOccurrenceStatus
{
    /// <summary>已安排，尚未向任何通道发出展示请求。</summary>
    Pending = 0,

    /// <summary>展示请求已被通道接收（“已请求/已交接”），不代表用户已看到。</summary>
    Requested = 1,

    /// <summary>用户选择稍后提醒，该实例结束。</summary>
    Snoozed = 2,

    /// <summary>用户选择已完成。</summary>
    Completed = 3,

    /// <summary>明确投递失败，等待人工处理。</summary>
    Failed = 4,

    /// <summary>投递结果不确定，展示层核对历史；人工重发必须显式操作。</summary>
    Uncertain = 5,

    /// <summary>因计划修改、取消、事项完成或历史版本不明而作废。</summary>
    Cancelled = 6,
}

/// <summary>
/// 提醒实例与操作（设计 7.5）。选项规则快照保证旧提醒含义不随设置变化；
/// plan_version 为创建时的计划版本快照，动作必须与当前计划版本一致。
/// </summary>
public sealed record ReminderOccurrence(
    Guid Id,
    Guid ReminderId,
    Guid ItemId,
    DateTimeOffset ScheduledAtUtc,
    ReminderOccurrenceStatus Status,
    string NotificationTag,
    IReadOnlyList<SnoozeOption> OptionsSnapshot,
    DateTimeOffset? RequestedAtUtc,
    string? Action,
    DateTimeOffset? ActionAtUtc,
    int AttemptCount,
    string? LastError,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int PlanVersion = 0,
    DateTimeOffset? HandedOffAtUtc = null,
    string? Channel = null,
    int DeliveryAttempts = 0,
    int RecoveryAttempts = 0,
    DateTimeOffset? DeliveryLeaseUntilUtc = null,
    string? DeliveryOwner = null,
    string? CancelReason = null,
    int ManualAttempts = 0);
