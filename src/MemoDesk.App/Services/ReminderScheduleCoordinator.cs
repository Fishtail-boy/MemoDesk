using MemoDesk.Core.Items;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Time;
using MemoDesk.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace MemoDesk.App.Services;

/// <summary>
/// 计划通知运行时：先持久化登记意图 → 调用系统 → 按计划版本条件写回；
/// 旧 tag 独立撤销；启动/周期对账区分查询失败与成功为空；到期缺失时查历史证据。
/// </summary>
public sealed class ReminderScheduleCoordinator
{
    private static readonly TimeSpan Horizon = TimeSpan.FromDays(7);

    private readonly IReminderRepository _reminders;
    private readonly IReminderNotificationGateway _gateway;
    private readonly ItemService _items;
    private readonly AppSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<ReminderScheduleCoordinator> _logger;

    public ReminderScheduleCoordinator(
        IReminderRepository reminders,
        IReminderNotificationGateway gateway,
        ItemService items,
        AppSettings settings,
        TimeProvider clock,
        ILogger<ReminderScheduleCoordinator> logger)
    {
        _reminders = reminders;
        _gateway = gateway;
        _items = items;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    public void Reconcile()
    {
        if (!_gateway.IsAvailable || !_gateway.IsSchedulingSupported)
        {
            return;
        }

        ProcessCancellations();

        var now = _clock.GetUtcNow();
        var plans = _reminders.GetActiveBetween(now.AddYears(-1), now + Horizon);

        if (!_settings.UseSystemNotifications)
        {
            // 关闭系统通知：保持 Windows 所有权，撤销成功后才由 ProcessCancellations 交回 app。
            foreach (var plan in plans)
            {
                if (!string.IsNullOrEmpty(plan.ScheduledTag)
                    && plan.ScheduleState is "scheduled" or "registering" or "revoking" or "uncertain")
                {
                    _reminders.BeginScheduleRevocation(plan.Id, "system-notifications-disabled", now);
                }
            }

            ProcessCancellations();
            return;
        }

        var scheduledSnapshot = _gateway.QueryScheduled();
        if (!scheduledSnapshot.QuerySucceeded)
        {
            // 查询失败：保持现状，不做任何破坏性登记/撤销，等待下次核对。
            _logger.LogWarning("查询系统计划通知失败：{Error}", scheduledSnapshot.Error);
            return;
        }

        var scheduledTags = new HashSet<string>(scheduledSnapshot.Tags, StringComparer.Ordinal);

        NotificationHistorySnapshot? history = null;
        foreach (var plan in plans)
        {
            var tag = plan.ScheduledTag;
            var state = plan.ScheduleState;

            if (!string.IsNullOrEmpty(tag) && state is "scheduled" or "registering")
            {
                if (scheduledTags.Contains(tag))
                {
                    if (state == "registering")
                    {
                        // 登记调用成功但写回前崩溃：系统里已有该 tag，条件写回 scheduled。
                        _reminders.CompleteScheduleRegistration(
                            plan.Id, plan.PlanVersion, plan.ReminderAtUtc, true, null, now);
                    }

                    continue;
                }

                if (plan.ReminderAtUtc > now)
                {
                    // 未来计划缺失：安全重新登记。
                    Register(plan, now);
                    continue;
                }

                // 已到期且 tag 不在计划列表：先查历史证据。
                history ??= _gateway.QueryHistory();
                if (!history.QuerySucceeded)
                {
                    _logger.LogWarning("查询通知历史失败，保留登记状态等待重试：{Error}", history.Error);
                    continue;
                }

                if (history.Tags.Contains(tag, StringComparer.Ordinal))
                {
                    var occurrence = _reminders.GetBlockingOccurrenceForPlan(plan.Id, plan.PlanVersion);
                    if (occurrence is not null
                        && occurrence.Status is ReminderOccurrenceStatus.Pending or ReminderOccurrenceStatus.Requested)
                    {
                        _reminders.MarkOccurrenceStatus(
                            occurrence.Id, ReminderOccurrenceStatus.Requested, "system-history", now);
                    }

                    _reminders.CompleteScheduleRegistration(
                        plan.Id, plan.PlanVersion, plan.ReminderAtUtc, true, null, now);
                }
                else
                {
                    // 无投递证据：单事务标记待核对；展示层只查历史，不自动重发同一实例。
                    var occurrence = _reminders.GetBlockingOccurrenceForPlan(plan.Id, plan.PlanVersion);
                    if (occurrence is null
                        || !_reminders.MarkScheduledDeliveryUnknown(plan.Id, plan.PlanVersion, occurrence.Id, now))
                    {
                        _logger.LogWarning("无法将到期计划标记为投递未知：{ReminderId}", plan.Id);
                    }
                }

                continue;
            }

            if (plan.ReminderAtUtc > now && state is null or "failed")
            {
                Register(plan, now);
            }
        }

        // 系统中存在、但不属于任何当前登记或撤销任务的 tag：直接撤销并在下次核对重试。
        var referencedTags = plans
            .Select(plan => plan.ScheduledTag)
            .Where(tag => !string.IsNullOrEmpty(tag))
            .ToHashSet(StringComparer.Ordinal);
        var pendingCancellationTags = _reminders.GetPendingCancellations(200)
            .Select(cancellation => cancellation.Tag)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var orphan in scheduledTags)
        {
            if (!referencedTags.Contains(orphan) && !pendingCancellationTags.Contains(orphan))
            {
                _logger.LogInformation("撤销孤儿计划通知：{Tag}", orphan);
                _gateway.CancelScheduled(orphan);
            }
        }
    }

    /// <summary>主动退出：把当前计划通知加入独立撤销任务并尝试撤销。</summary>
    public void RevokeAllScheduled()
    {
        if (!_gateway.IsSchedulingSupported)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        var plans = _reminders.GetActiveBetween(now, now + Horizon);
        foreach (var plan in plans)
        {
            if (!string.IsNullOrEmpty(plan.ScheduledTag)
                && plan.ScheduleState is "scheduled" or "registering" or "revoking" or "uncertain")
            {
                _reminders.BeginScheduleRevocation(plan.Id, "app-exit", now);
            }
        }

        ProcessCancellations();
    }

    private void Register(ReminderPlan plan, DateTimeOffset now)
    {
        // 短倒计时由应用唤醒，届时仍调用 Windows 即时通知；不与系统计划重复计时。
        if (plan.Source == "LocalExplicit" && plan.RelativeSeconds is > 0 and <= 300
            && plan.ReminderAtUtc > now && string.IsNullOrEmpty(plan.ScheduledTag)) return;
        if (!string.IsNullOrEmpty(plan.ScheduledTag)
            && plan.ScheduleState == "revoking")
        {
            // 撤销仍在进行，不登记新计划。
            return;
        }

        var item = _items.Get(plan.ItemId);
        if (item is null || item.Status != Core.Domain.ItemStatus.Open)
        {
            return;
        }

        var tag = $"memo-{plan.ItemId:N}-{plan.PlanVersion}";
        var occurrenceId = _reminders.BeginScheduleRegistration(
            plan.Id,
            plan.PlanVersion,
            plan.ReminderAtUtc,
            tag,
            _settings.SnoozeOptions,
            now);

        if (occurrenceId is null)
        {
            return;
        }

        var occurrence = _reminders.GetOccurrence(occurrenceId.Value);
        var options = occurrence?.OptionsSnapshot ?? _settings.SnoozeOptions;
        var timeText = ViewModels.RowFormat.ReminderTime(plan.ReminderAtUtc, _settings.TimeZone);
        var scheduled = new ScheduledReminder(
            tag,
            occurrenceId.Value,
            item.Content,
            $"{ViewModels.RowFormat.TypeText(item.Type)} · {timeText}",
            timeText,
            plan.ReminderAtUtc,
            options);

        NotificationScheduleOutcome outcome;
        try
        {
            outcome = _gateway.Schedule(scheduled);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "计划通知登记异常");
            outcome = NotificationScheduleOutcome.DefiniteFailure;
        }

        var success = outcome == NotificationScheduleOutcome.Scheduled;
        var wroteBack = _reminders.CompleteScheduleRegistration(
            plan.Id,
            plan.PlanVersion,
            plan.ReminderAtUtc,
            success,
            success ? null : outcome.ToString(),
            now);

        if (!wroteBack)
        {
            // 登记期间计划被修改/取消：撤销刚登记的旧 tag，不能覆盖新计划。
            _reminders.EnqueueCancellation(plan.Id, plan.PlanVersion, tag, "registration-stale", now);
            _gateway.CancelScheduled(tag);
            ProcessCancellations();
        }
    }

    private void ProcessCancellations()
    {
        var now = _clock.GetUtcNow();
        foreach (var cancellation in _reminders.GetPendingCancellations(20))
        {
            var success = _gateway.CancelScheduled(cancellation.Tag);
            _reminders.MarkCancellationResult(
                cancellation.Id,
                success,
                success ? null : "cancel-failed",
                now);

            if (success)
            {
                // 撤销成功后才把投递所有权交回应用；失败时保持 windows/revoking。
                _reminders.CompleteScheduleRevocation(cancellation.ReminderId, cancellation.Tag, now);
            }
            else if (cancellation.Attempts + 1 >= 3)
            {
                _reminders.MarkScheduleUncertain(
                    cancellation.ReminderId, cancellation.Tag, "cancel-failed", now);
            }
        }
    }
}
