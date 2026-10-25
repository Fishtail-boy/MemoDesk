namespace MemoDesk.Core.Reminders;

public enum NotificationDeliveryOutcome
{
    /// <summary>通道已接收（已请求/已交接），不代表用户已经看到。</summary>
    HandedOff = 0,

    /// <summary>通道明确失败，可回落到应用内提醒。</summary>
    DefiniteFailure = 1,

    /// <summary>结果不确定；只核对系统历史，避免自动换通道或重发造成重复。</summary>
    Uncertain = 2,
}

public enum NotificationScheduleOutcome
{
    Scheduled = 0,
    DefiniteFailure = 1,
    Unsupported = 2,
}

/// <summary>即时提醒通知内容（由应用层按业务时区格式化）。</summary>
public sealed record ReminderNotification(
    Guid OccurrenceId,
    string Tag,
    string Title,
    string Body,
    string TimeText,
    bool IsCatchUp,
    IReadOnlyList<SnoozeOption> Options);

/// <summary>计划通知：由 Windows 在指定时刻投递。</summary>
public sealed record ScheduledReminder(
    string Tag,
    Guid OccurrenceId,
    string Title,
    string Body,
    string TimeText,
    DateTimeOffset DeliverAtUtc,
    IReadOnlyList<SnoozeOption> Options);

/// <summary>系统计划通知查询快照：必须区分“成功但为空”和“查询失败”。</summary>
public sealed record NotificationScheduleSnapshot(
    bool QuerySucceeded,
    IReadOnlyList<string> Tags,
    string? Error);

/// <summary>通知历史查询快照（用于已到期提醒的投递证据）。</summary>
public sealed record NotificationHistorySnapshot(
    bool QuerySucceeded,
    IReadOnlyList<string> Tags,
    string? Error);

/// <summary>旧计划通知的撤销任务；独立于计划当前版本，直到撤销成功。</summary>
public sealed record NotificationCancellation(
    Guid Id,
    Guid ReminderId,
    int PlanVersion,
    string Tag,
    string Reason,
    string State,
    int Attempts,
    string? LastError);

/// <summary>用户点击通知按钮/选择后的动作。</summary>
public sealed record ReminderNotificationAction(
    Guid OccurrenceId,
    string Action,
    string? OptionId);

/// <summary>
/// 提醒通知网关：即时投递、计划登记/撤销/查询、历史查询、动作回调。
/// 查询失败必须可区分，调用方据此决定是否做破坏性对账。
/// </summary>
public interface IReminderNotificationGateway
{
    bool IsAvailable { get; }

    bool IsSchedulingSupported { get; }

    NotificationDeliveryOutcome SendNow(ReminderNotification notification);

    NotificationScheduleOutcome Schedule(ScheduledReminder reminder);

    /// <summary>撤销指定 tag；返回是否确认成功。</summary>
    bool CancelScheduled(string notificationTag);

    NotificationScheduleSnapshot QueryScheduled();

    NotificationHistorySnapshot QueryHistory();

    event Action<ReminderNotificationAction>? ActionInvoked;
}
