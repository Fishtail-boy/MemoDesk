using MemoDesk.Core.Reminders;

namespace MemoDesk.Core.Reminders;

public enum ManualRetryOutcome
{
    Reset = 0,
    NotEligible = 1,
    PlanChanged = 2,
    ItemNotOpen = 3,
    InFlight = 4,
}

public interface IReminderRepository
{
    IReadOnlyList<ReminderPlan> GetActiveDue(DateTimeOffset atUtc);

    ReminderPlan? GetActiveForItem(Guid itemId);

    ReminderPlan? GetReminder(Guid reminderId);

    /// <summary>启用且提醒时间在 [from, to] 内的计划，用于计划通知登记/对账。</summary>
    IReadOnlyList<ReminderPlan> GetActiveBetween(DateTimeOffset fromUtc, DateTimeOffset toUtc);

    /// <summary>尚未交接给系统或用户、可由应用计时的计划。</summary>
    IReadOnlyList<ReminderPlan> GetWakeablePlans() =>
        GetActiveBetween(DateTimeOffset.MinValue, DateTimeOffset.MaxValue)
            .Where(p => p.DeliveryOwner is not ("windows" or "windows-pending")
                && GetBlockingOccurrenceForPlan(p.Id, p.PlanVersion) is null).ToArray();

    ReminderOccurrence? GetOccurrence(Guid occurrenceId);

    /// <summary>
    /// 当前计划版本下阻止新建实例的实例：Pending / Requested / Uncertain / Failed。
    /// Failed 也阻止自动重建，必须由用户显式人工重发。
    /// </summary>
    ReminderOccurrence? GetBlockingOccurrenceForPlan(Guid reminderId, int planVersion);

    IReadOnlyList<ReminderOccurrence> GetPendingDue(DateTimeOffset atUtc);

    /// <summary>租约已过期的待投递实例，或可自动恢复一次的不确定实例。</summary>
    IReadOnlyList<ReminderOccurrence> GetRecoverable(DateTimeOffset atUtc);

    /// <summary>插入实例；同计划版本已存在正版本实例时返回 false（唯一索引）。</summary>
    bool TryAddOccurrence(ReminderOccurrence occurrence);

    /// <summary>原子领取首次投递：状态 Pending、未尝试过、租约空闲。</summary>
    bool TryClaimDelivery(Guid occurrenceId, string owner, DateTimeOffset now, TimeSpan leaseDuration);

    /// <summary>单调时钟确认短时相对提醒到期；UTC 回拨时仍允许领取，仓储必须校验来源与当前版本。</summary>
    bool TryClaimElapsedDelivery(Guid occurrenceId, string owner, DateTimeOffset now, TimeSpan leaseDuration) =>
        TryClaimDelivery(occurrenceId, owner, now, leaseDuration);

    /// <summary>
    /// 原子领取恢复投递：仅限“Pending 且已有尝试”的崩溃窗口，或 Uncertain 且恢复次数小于 1；
    /// 领取时会把实例置为 Uncertain 并增加恢复计数。
    /// </summary>
    bool TryClaimRecovery(Guid occurrenceId, string owner, DateTimeOffset now, TimeSpan leaseDuration);

    /// <summary>通道已接收展示请求（“已请求/已交接”）；只允许领取者调用。</summary>
    bool MarkHandedOff(Guid occurrenceId, string owner, string channel, DateTimeOffset now);

    bool MarkDeliveryFailed(Guid occurrenceId, string owner, string? error, DateTimeOffset now);

    bool MarkDeliveryUncertain(Guid occurrenceId, string owner, string? error, DateTimeOffset now);

    /// <summary>把实例标记为指定终态（人工操作/清理路径）。</summary>
    bool MarkOccurrenceStatus(
        Guid occurrenceId,
        ReminderOccurrenceStatus status,
        string? action,
        DateTimeOffset atUtc);

    /// <summary>
    /// 设置或取消事项的提醒：null 表示关闭提醒；同一事项只保留一个有效计划。
    /// 修改/取消时所有未处理实例（Pending/Requested/Uncertain）作废，避免旧动作生效。
    /// </summary>
    void SetReminder(Guid itemId, DateTimeOffset? reminderAtUtc, DateTimeOffset now);

    /// <summary>
    /// 人工重发：仅允许 Failed、Uncertain，或“领取后崩溃且恢复预算耗尽”的 Pending。
    /// 单事务内同时校验租约、计划启用/版本与事项状态；复用同一实例，不重置自动恢复预算。
    /// </summary>
    ManualRetryOutcome TryResetOccurrenceForManualDelivery(Guid occurrenceId, DateTimeOffset now);

    /// <summary>
    /// 事务 A：为该计划版本确保唯一 Pending 实例，并持久化“登记中”意图。
    /// 返回实例 ID；计划已变更/停用或并发冲突时返回 null。
    /// </summary>
    Guid? BeginScheduleRegistration(
        Guid reminderId,
        int planVersion,
        DateTimeOffset scheduledAtUtc,
        string notificationTag,
        IReadOnlyList<SnoozeOption> optionsSnapshot,
        DateTimeOffset now);

    /// <summary>
    /// 事务 B：按计划版本与提醒时间条件写回登记结果；期间计划被修改时返回 false，
    /// 调用方必须取消刚登记的系统通知作为补偿。
    /// </summary>
    bool CompleteScheduleRegistration(
        Guid reminderId,
        int planVersion,
        DateTimeOffset scheduledAtUtc,
        bool success,
        string? error,
        DateTimeOffset now);

    /// <summary>独立保存旧通知撤销任务（同一 tag 的未完成撤销只保留一条）。</summary>
    void EnqueueCancellation(
        Guid reminderId,
        int planVersion,
        string notificationTag,
        string reason,
        DateTimeOffset now);

    IReadOnlyList<NotificationCancellation> GetPendingCancellations(int limit);

    void MarkCancellationResult(
        Guid cancellationId,
        bool success,
        string? error,
        DateTimeOffset now);

    /// <summary>开始撤销计划通知：保持 windows 所有权，状态改为 revoking，并落撤销任务。</summary>
    bool BeginScheduleRevocation(Guid reminderId, string reason, DateTimeOffset now);

    /// <summary>撤销成功后交回应用投递：清空登记字段、delivery_owner=app。</summary>
    bool CompleteScheduleRevocation(Guid reminderId, string notificationTag, DateTimeOffset now);

    /// <summary>撤销长期失败：保持 windows 所有权，标记 uncertain 供页面提示。</summary>
    bool MarkScheduleUncertain(Guid reminderId, string notificationTag, string error, DateTimeOffset now);

    /// <summary>
    /// 到期计划在系统与历史中都无投递证据：单事务内写回计划失败（owner=app）并把实例
    /// 置为 Uncertain（attempts≥1），任一步版本/状态不匹配则整体回滚。
    /// </summary>
    bool MarkScheduledDeliveryUnknown(Guid reminderId, int planVersion, Guid occurrenceId, DateTimeOffset now);

    /// <summary>
    /// 稍后提醒：同一事务内校验实例版本、计划启用状态与事项状态，然后关闭当前实例并把
    /// 提醒计划推到新时间（计划版本 +1）。只改下一次提醒，不改事项日期。
    /// </summary>
    bool SnoozeOccurrence(Guid occurrenceId, DateTimeOffset newReminderAtUtc, string optionId, DateTimeOffset now);
}
