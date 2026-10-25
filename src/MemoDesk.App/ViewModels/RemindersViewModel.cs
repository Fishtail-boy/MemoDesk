using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoDesk.App.Services;
using MemoDesk.Core;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Time;
using MemoDesk.Infrastructure.Queries;
using MemoDesk.Infrastructure.Settings;

namespace MemoDesk.App.ViewModels;

public sealed record ReminderRow(
    Guid ReminderId,
    Guid ItemId,
    string Content,
    string TypeText,
    string PlanText,
    string StatusText,
    string DetailText,
    bool IsProblem,
    bool CanRequestNow);

public sealed record ReminderFilterOption(ReminderViewFilter Filter, string Label);

/// <summary>提醒状态页：未提醒 / 已请求 / 已稍后 / 异常（设计 5.5）。</summary>
public partial class RemindersViewModel : ObservableObject
{
    private readonly ReminderQueryService _query;
    private readonly IReminderRepository _reminders;
    private readonly ItemService _items;
    private readonly ReminderPresenter _presenter;
    private readonly AppSettings _settings;
    private readonly TimeProvider _clock;
    private readonly DialogService _dialogs;

    public RemindersViewModel(
        ReminderQueryService query,
        IReminderRepository reminders,
        ItemService items,
        ReminderPresenter presenter,
        AppSettings settings,
        TimeProvider clock,
        DialogService dialogs)
    {
        _query = query;
        _reminders = reminders;
        _items = items;
        _presenter = presenter;
        _settings = settings;
        _clock = clock;
        _dialogs = dialogs;
    }

    public IReadOnlyList<ReminderFilterOption> Filters { get; } = new[]
    {
        new ReminderFilterOption(ReminderViewFilter.Unreminded, "未提醒"),
        new ReminderFilterOption(ReminderViewFilter.Requested, "已请求"),
        new ReminderFilterOption(ReminderViewFilter.Snoozed, "已稍后"),
        new ReminderFilterOption(ReminderViewFilter.Problem, "异常"),
        new ReminderFilterOption(ReminderViewFilter.All, "全部"),
    };

    public ObservableCollection<ReminderRow> Rows { get; } = new();

    [ObservableProperty]
    private ReminderViewFilter filter = ReminderViewFilter.Unreminded;

    [ObservableProperty]
    private string emptyHint = "当前筛选没有提醒。";

    [ObservableProperty]
    private string message = string.Empty;

    public void Refresh()
    {
        var timeZone = _settings.TimeZone;
        Rows.Clear();

        foreach (var row in _query.GetOverview().Where(row => ReminderQueryService.MatchesFilter(row, Filter)))
        {
            Rows.Add(BuildRow(row, timeZone));
        }

        EmptyHint = Filter switch
        {
            ReminderViewFilter.Unreminded => "没有等待中的提醒。",
            ReminderViewFilter.Requested => "还没有已交接的提醒。",
            ReminderViewFilter.Snoozed => "没有稍后提醒中的事项。",
            ReminderViewFilter.Problem => "没有异常提醒。",
            _ => "还没有任何提醒。",
        };
    }

    [RelayCommand]
    private void SetFilter(string filter)
    {
        Filter = filter switch
        {
            "requested" => ReminderViewFilter.Requested,
            "snoozed" => ReminderViewFilter.Snoozed,
            "problem" => ReminderViewFilter.Problem,
            "all" => ReminderViewFilter.All,
            _ => ReminderViewFilter.Unreminded,
        };
        Refresh();
    }

    [RelayCommand]
    private void RequestNow(ReminderRow row)
    {
        var item = _items.Get(row.ItemId);
        if (item is null || item.Status != ItemStatus.Open)
        {
            Message = "事项已完成或已删除，无法立即提醒。";
            return;
        }

        var plan = _reminders.GetActiveForItem(row.ItemId);
        if (plan is null)
        {
            Message = "提醒计划已停用，请先编辑事项重新设置提醒。";
            return;
        }

        var now = _clock.GetUtcNow();
        var blocking = _reminders.GetBlockingOccurrenceForPlan(plan.Id, plan.PlanVersion);
        if (blocking is not null)
        {
            if (blocking.Status is ReminderOccurrenceStatus.Requested
                || (blocking.Status == ReminderOccurrenceStatus.Pending
                    && blocking.DeliveryAttempts == 0))
            {
                Message = "该事项已有未处理完的提醒实例。";
                return;
            }

            var outcome = _reminders.TryResetOccurrenceForManualDelivery(blocking.Id, now);
            Message = outcome switch
            {
                ManualRetryOutcome.Reset => "已请求立即提醒。",
                ManualRetryOutcome.InFlight => "提醒正在投递中，请稍后再试。",
                ManualRetryOutcome.PlanChanged => "提醒计划已变更，请刷新后再试。",
                ManualRetryOutcome.ItemNotOpen => "事项已完成或已删除，无法立即提醒。",
                _ => "当前提醒状态不允许人工重发。",
            };

            if (outcome == ManualRetryOutcome.Reset)
            {
                _presenter.CheckDue();
            }

            Refresh();
            return;
        }

        var occurrence = new ReminderOccurrence(
            Guid.NewGuid(),
            plan.Id,
            row.ItemId,
            now,
            ReminderOccurrenceStatus.Pending,
            $"manual-{row.ItemId:N}-{plan.PlanVersion}",
            _settings.SnoozeOptions,
            null,
            null,
            null,
            0,
            null,
            now,
            now,
            PlanVersion: plan.PlanVersion);

        if (!_reminders.TryAddOccurrence(occurrence))
        {
            Message = "该事项的提醒已由其他操作创建，请刷新。";
            Refresh();
            return;
        }

        Message = "已请求立即提醒。";
        _presenter.CheckDue();
        Refresh();
    }

    [RelayCommand]
    private void Complete(ReminderRow row)
    {
        if (_items.Complete(row.ItemId))
        {
            Message = "事项已标记完成，提醒已停止。";
        }

        Refresh();
    }

    [RelayCommand]
    private void Edit(ReminderRow row)
    {
        var item = _items.Get(row.ItemId);
        if (item is not null && _dialogs.EditItem(item, _settings.TimeZone))
        {
            Refresh();
        }
    }

    private ReminderRow BuildRow(ReminderOverviewRow row, TimeZoneInfo timeZone)
    {
        var statusText = row.OccurrenceStatus switch
        {
            null => row.PlanEnabled ? "未提醒（等待生成实例）" : "已停用",
            ReminderOccurrenceStatus.Pending => "未提醒",
            ReminderOccurrenceStatus.Requested => "已请求 / 已交接",
            ReminderOccurrenceStatus.Snoozed => "已稍后",
            ReminderOccurrenceStatus.Completed => "已完成",
            ReminderOccurrenceStatus.Failed => "投递失败",
            ReminderOccurrenceStatus.Uncertain => "待核对（未自动重发）",
            ReminderOccurrenceStatus.Cancelled => "已作废",
            _ => "未知",
        };

        var detailParts = new List<string>();
        detailParts.Add($"计划 {RowFormat.ReminderTime(row.PlanAtUtc, timeZone)}");
        if (!row.PlanEnabled)
        {
            detailParts.Add("计划已停用");
        }

        if (row.HandedOffAtUtc is { } handedOff)
        {
            detailParts.Add($"交接 {BusinessTime.FormatLocal(handedOff, timeZone, "MM-dd HH:mm")}");
        }

        if (row.ActionAtUtc is { } actionAt)
        {
            detailParts.Add($"操作 {BusinessTime.FormatLocal(actionAt, timeZone, "MM-dd HH:mm")}");
        }

        if (row.RecoveryAttempts > 0)
        {
            detailParts.Add($"恢复 {row.RecoveryAttempts} 次");
        }

        if (row.OccurrenceStatus is ReminderOccurrenceStatus.Failed or ReminderOccurrenceStatus.Uncertain or ReminderOccurrenceStatus.Cancelled
            && !string.IsNullOrWhiteSpace(row.LastError))
        {
            detailParts.Add(row.LastError!);
        }

        var isProblem = row.OccurrenceStatus is ReminderOccurrenceStatus.Failed
            or ReminderOccurrenceStatus.Uncertain
            or ReminderOccurrenceStatus.Cancelled;

        return new ReminderRow(
            row.ReminderId,
            row.ItemId,
            row.Content,
            RowFormat.TypeText(row.ItemType),
            detailParts[0],
            statusText,
            string.Join("　", detailParts.Skip(1)),
            isProblem,
            row.PlanEnabled);
    }
}
