using System;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Time;
using MemoDesk.Infrastructure.Settings;
using MemoDesk.App.Views;
using Microsoft.Extensions.Logging;

namespace MemoDesk.App.Services;

public sealed record PendingReminder(ReminderOccurrence Occurrence, Item Item, bool IsRecovery, bool ElapsedDeadline = false);

/// <summary>
/// 提醒展示：数据库计划 → 到期候选 → 原子领取 → 展示 → 交接落库。
/// 只有展示成功才记为 Requested；交接结果未知时只核对，不自动换通道或重发。
/// </summary>
public sealed class ReminderPresenter
{
    private readonly ReminderScheduler _scheduler;
    private readonly ItemService _items;
    private readonly IReminderRepository _reminders;
    private readonly IReminderNotificationGateway _gateway;
    private readonly AppSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<ReminderPresenter> _logger;

    private readonly Queue<PendingReminder> _queue = new();
    private readonly HashSet<Guid> _queuedOccurrences = new();
    private InAppReminderWindow? _current;
    private Guid? _currentOccurrenceId;
    private readonly Dictionary<Guid, (DateTimeOffset Next, int Checks)> _uncertain = new();

    public ReminderPresenter(
        ReminderScheduler scheduler,
        ItemService items,
        IReminderRepository reminders,
        IReminderNotificationGateway gateway,
        AppSettings settings,
        TimeProvider clock,
        ILogger<ReminderPresenter> logger)
    {
        _scheduler = scheduler;
        _items = items;
        _reminders = reminders;
        _gateway = gateway;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    public event Action? ItemsChanged;

    /// <summary>点击通知正文：请求打开对应事项详情。</summary>
    public event Action<Guid>? OpenItemRequested;

    public void CheckDue(IReadOnlyList<ReminderPlan>? monotonicDue = null)
    {
        var now = _clock.GetUtcNow();
        CheckUncertain(now);

        IReadOnlyList<ReminderCandidate> candidates;
        try
        {
            candidates = _scheduler.CollectCandidates(now, _settings.SnoozeOptions, monotonicDue,
                requireElapsedForShortRelative: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "提醒扫描失败");
            return;
        }

        foreach (var candidate in candidates)
        {
            var occurrence = candidate.Occurrence;
            if (occurrence.Status == ReminderOccurrenceStatus.Uncertain)
            {
                _uncertain.TryAdd(occurrence.Id, (now, 0));
                continue;
            }
            if (_queuedOccurrences.Contains(occurrence.Id) || _currentOccurrenceId == occurrence.Id)
            {
                continue;
            }

            var item = _items.Get(occurrence.ItemId);
            if (item is null || item.Status != ItemStatus.Open)
            {
                _reminders.MarkOccurrenceStatus(
                    occurrence.Id, ReminderOccurrenceStatus.Cancelled, "item-not-open", now);

                // 事项已不可提醒：停止计划，避免重复建档。
                _reminders.SetReminder(occurrence.ItemId, null, now);
                continue;
            }

            _queuedOccurrences.Add(occurrence.Id);
            _queue.Enqueue(new PendingReminder(occurrence, item, candidate.IsRecovery, candidate.ElapsedDeadline));
        }

        _current?.SetQueueCount(_queue.Count);
        CheckUncertain(now);
        ShowNext();
        if (candidates.Count > 0) ItemsChanged?.Invoke();
    }

    private void ShowNext()
    {
        if (_current is not null)
        {
            return;
        }

        if (System.Windows.Application.Current?.Dispatcher.HasShutdownStarted == true)
        {
            _queue.Clear();
            _queuedOccurrences.Clear();
            return;
        }

        while (_current is null && _queue.Count > 0)
        {
            var pending = _queue.Dequeue();
            _queuedOccurrences.Remove(pending.Occurrence.Id);

            var occurrence = _reminders.GetOccurrence(pending.Occurrence.Id);
            if (occurrence is null
                || occurrence.Status is not (ReminderOccurrenceStatus.Pending
                    or ReminderOccurrenceStatus.Requested
                    or ReminderOccurrenceStatus.Uncertain))
            {
                continue;
            }

            // 已交给 Windows（含登记中/撤销中）的计划由系统通道负责；协调器确认撤销成功后才会交回 app。
            var plan = _reminders.GetReminder(occurrence.ReminderId);
            if (plan is { DeliveryOwner: "windows" or "windows-pending" })
            {
                continue;
            }

            var now = _clock.GetUtcNow();
            var owner = $"presenter-{Environment.ProcessId}-{Guid.NewGuid():N}";
            var candidate = new ReminderCandidate(occurrence, pending.IsRecovery, pending.ElapsedDeadline);

            if (!_scheduler.TryClaim(candidate, owner, now))
            {
                continue;
            }

            var item = _items.Get(occurrence.ItemId);
            if (item is null || item.Status != ItemStatus.Open)
            {
                _scheduler.MarkFailed(occurrence.Id, owner, "item-not-open", now);
                continue;
            }

            var display = new PendingReminder(
                occurrence,
                item,
                pending.IsRecovery || ReminderScheduler.IsCatchUp(occurrence, now));

            if (_settings.UseSystemNotifications && _gateway.IsAvailable)
            {
                var notification = new ReminderNotification(
                    occurrence.Id,
                    occurrence.NotificationTag,
                    item.Content,
                    $"备忘提醒 · {ViewModels.RowFormat.ReminderTime(occurrence.ScheduledAtUtc, _settings.TimeZone)}",
                    ViewModels.RowFormat.ReminderTime(occurrence.ScheduledAtUtc, _settings.TimeZone),
                    display.IsRecovery,
                    occurrence.OptionsSnapshot);

                NotificationDeliveryOutcome outcome;
                try
                {
                    outcome = _gateway.SendNow(notification);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "系统通知投递结果未知，等待核对");
                    outcome = NotificationDeliveryOutcome.Uncertain;
                }

                if (outcome == NotificationDeliveryOutcome.HandedOff)
                {
                    if (!_scheduler.MarkHandedOff(occurrence.Id, owner, "windows", now))
                    {
                        _logger.LogWarning("系统通知交接落库失败：{OccurrenceId}", occurrence.Id);
                    }

                    continue;
                }
                if (outcome == NotificationDeliveryOutcome.Uncertain)
                {
                    _scheduler.MarkUncertain(occurrence.Id, owner, "系统通知交接结果未知，待核对", now);
                    _uncertain.TryAdd(occurrence.Id, (now, 0));
                    CheckUncertain(now);
                    continue;
                }

                _logger.LogWarning("系统通知未成功（{Outcome}），使用应用内提醒兜底", outcome);
            }

            if (!_settings.AllowFallbackNotifications)
            {
                _scheduler.MarkFailed(occurrence.Id, owner, "系统通知不可用，且应用内兜底已关闭", now);
                continue;
            }

            var window = new InAppReminderWindow(display, _settings.TimeZone);
            window.SetQueueCount(_queue.Count);
            _current = window;
            _currentOccurrenceId = occurrence.Id;

            window.CompleteRequested += () => HandleComplete(window, display, owner);
            window.SnoozeRequested += optionId => HandleSnooze(window, display, optionId);
            window.Closed += (_, _) =>
            {
                _current = null;
                _currentOccurrenceId = null;
                ShowNext();
            };

            try
            {
                window.Show();
                if (!_scheduler.MarkHandedOff(occurrence.Id, owner, "inapp", _clock.GetUtcNow()))
                {
                    _logger.LogWarning("提醒交接落库失败：{OccurrenceId}", occurrence.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "提醒展示失败：{OccurrenceId}", occurrence.Id);
                _scheduler.MarkFailed(occurrence.Id, owner, ex.Message, _clock.GetUtcNow());
                _current = null;
                _currentOccurrenceId = null;
            }
        }
    }

    private void CheckUncertain(DateTimeOffset now)
    {
        foreach (var pair in _uncertain.ToArray())
        {
            if (pair.Value.Checks >= 2 || pair.Value.Next > now) continue;
            var occurrence = _reminders.GetOccurrence(pair.Key);
            if (occurrence is null || occurrence.Status != ReminderOccurrenceStatus.Uncertain)
            { _uncertain.Remove(pair.Key); continue; }
            var plan = _reminders.GetReminder(occurrence.ReminderId);
            if (plan is null || !plan.Enabled || plan.PlanVersion != occurrence.PlanVersion)
            { _uncertain.Remove(pair.Key); continue; }
            try
            {
                var history = _gateway.QueryHistory();
                if (history.QuerySucceeded && history.Tags.Contains(occurrence.NotificationTag, StringComparer.Ordinal))
                {
                    _reminders.MarkOccurrenceStatus(occurrence.Id, ReminderOccurrenceStatus.Requested, "system-history", now);
                    _uncertain.Remove(pair.Key);
                    ItemsChanged?.Invoke();
                    continue;
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "核对通知历史失败"); }
            _uncertain[pair.Key] = (now.AddSeconds(30), pair.Value.Checks + 1);
        }
    }

    private void HandleComplete(InAppReminderWindow window, PendingReminder pending, string owner)
    {
        var completed = _items.Complete(pending.Item.Id, pending.Occurrence.Id);
        if (!completed)
        {
            _logger.LogWarning("提醒已完成操作被拒绝（实例已失效）：{OccurrenceId}", pending.Occurrence.Id);
        }
        else
        {
            ItemsChanged?.Invoke();
        }

        window.Close();
    }

    /// <summary>处理系统通知按钮/下拉选择（运行中或冷启动激活共用）。</summary>
    public void HandleAction(ReminderNotificationAction action)
    {
        var occurrence = _reminders.GetOccurrence(action.OccurrenceId);
        if (occurrence is null)
        {
            _logger.LogWarning("通知动作对应的实例不存在：{OccurrenceId}", action.OccurrenceId);
            return;
        }

        var item = _items.Get(occurrence.ItemId);
        if (item is null)
        {
            return;
        }

        switch (action.Action)
        {
            case "complete":
                if (_items.Complete(item.Id, occurrence.Id))
                {
                    ItemsChanged?.Invoke();
                }
                else
                {
                    _logger.LogWarning("旧通知的完成动作被拒绝：{OccurrenceId}", occurrence.Id);
                }

                break;
            case "snooze":
                var optionId = action.OptionId
                    ?? occurrence.OptionsSnapshot.FirstOrDefault()?.Id;
                if (optionId is not null)
                {
                    var result = _scheduler.Snooze(occurrence.Id, optionId, _settings.TimeZone, _clock.GetUtcNow());
                    if (!result.Success)
                    {
                        _logger.LogWarning("通知稍后提醒失败：{Error}", result.Error);
                    }
                    else
                    {
                        ItemsChanged?.Invoke();
                    }
                }

                break;
            default:
                _logger.LogInformation("通知正文点击：{OccurrenceId}", occurrence.Id);
                OpenItemRequested?.Invoke(item.Id);
                break;
        }
    }

    private void HandleSnooze(InAppReminderWindow window, PendingReminder pending, string optionId)
    {
        var result = _scheduler.Snooze(pending.Occurrence.Id, optionId, _settings.TimeZone, _clock.GetUtcNow());
        if (!result.Success)
        {
            _logger.LogWarning("稍后提醒失败：{Error}", result.Error);
        }
        else
        {
            ItemsChanged?.Invoke();
        }

        window.Close();
    }
}
