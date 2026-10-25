using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoDesk.App.Services;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Core.Parsing;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Time;
using MemoDesk.Infrastructure.Queries;
using MemoDesk.Infrastructure.Settings;

namespace MemoDesk.App.ViewModels;

/// <summary>备忘工作台：当前/已完成/已归档/全部；旧类型只作为来源资料，不显示类型选择。</summary>
public partial class MemosViewModel : ObservableObject
{
    public event Action? WidgetRequested;
    [RelayCommand] private void ToggleWidget() => WidgetRequested?.Invoke();
    private readonly ItemQueryService _query;
    private readonly ItemService _items;
    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private readonly IReminderRepository _reminders;
    private readonly TimeProvider _clock;
    private readonly HotkeyStatus _hotkeyStatus;
    private readonly IMemoParseRepository _parseResults;
    private readonly MemoParseCoordinator _parseCoordinator;

    public MemosViewModel(
        ItemQueryService query,
        ItemService items,
        AppSettings settings,
        DialogService dialogs,
        IReminderRepository reminders,
        TimeProvider clock,
        HotkeyStatus hotkeyStatus,
        IMemoParseRepository parseResults,
        MemoParseCoordinator parseCoordinator)
    {
        _query = query;
        _items = items;
        _settings = settings;
        _dialogs = dialogs;
        _reminders = reminders;
        _clock = clock;
        _hotkeyStatus = hotkeyStatus;
        _parseResults = parseResults;
        _parseCoordinator = parseCoordinator;
        _hotkeyStatus.PropertyChanged += (_, _) => OnPropertyChanged(nameof(HotkeyText));
        Filter = MemoFilter.Unprocessed;
    }

    public string HotkeyText => _hotkeyStatus.DisplayText;

    public ObservableCollection<MemoRow> Rows { get; } = new();

    /// <summary>用户点击“记一条”。由 App 打开快捷输入窗。</summary>
    public event Action? QuickCaptureRequested;

    [ObservableProperty]
    private MemoFilter filter;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string emptyHint = "暂无备忘。";

    public void Refresh()
    {
        var timeZone = _settings.TimeZone;
        var now = _clock.GetUtcNow();
        var query = SearchText.Trim();
        var rows = new List<(Item Item, DateTimeOffset? ReminderAt)>();

        foreach (var item in _query.GetUnifiedMemos(Filter, query))
        {
            rows.Add((item, _reminders.GetActiveForItem(item.Id)?.ReminderAtUtc));
        }

        var ordered = rows
            .OrderBy(row => row.ReminderAt is { } at && at <= now ? 0 : row.ReminderAt is null ? 2 : 1)
            .ThenBy(row => row.ReminderAt)
            .ThenByDescending(row => row.Item.CreatedAtUtc)
            .ToArray();

        Rows.Clear();
        foreach (var (item, reminderAt) in ordered)
        {
            var meta = BuildMeta(item, reminderAt, timeZone, now);
            if (BuildParseText(item) is { } parseText)
            {
                meta += $"　{parseText}";
            }

            Rows.Add(new MemoRow(
                item,
                item.Content,
                meta,
                StatusText(item),
                item.Status == ItemStatus.Open,
                CanRetryParse(item)));
        }

        EmptyHint = Filter switch
        {
            MemoFilter.Unprocessed => query.Length > 0 ? "没有匹配的备忘。" : "还没有备忘。按快捷键记一条。",
            MemoFilter.Completed => "还没有已完成的备忘。",
            MemoFilter.Archived => "没有已归档的备忘。",
            _ => "暂无备忘。",
        };
    }

    [RelayCommand]
    private void SetFilter(string filter)
    {
        Filter = filter switch
        {
            "completed" => MemoFilter.Completed,
            "archived" => MemoFilter.Archived,
            "all" => MemoFilter.All,
            _ => MemoFilter.Unprocessed,
        };
        Refresh();
    }

    [RelayCommand]
    private void Search() => Refresh();

    [RelayCommand]
    private void Capture() => QuickCaptureRequested?.Invoke();

    [RelayCommand]
    private void RetryParse(MemoRow row)
    {
        if (row.Item.CaptureId is { } captureId)
        {
            _parseCoordinator.Retry(captureId);
            Refresh();
        }
    }

    [RelayCommand]
    private void Complete(MemoRow row)
    {
        if (row.Item.Status == ItemStatus.Open)
        {
            _items.Complete(row.Item.Id);
            Refresh();
        }
    }

    [RelayCommand]
    private void Archive(MemoRow row)
    {
        if (row.Item.Status == ItemStatus.Open)
        {
            _items.Archive(row.Item.Id);
            Refresh();
        }
    }

    [RelayCommand]
    private void Reopen(MemoRow row)
    {
        if (row.Item.Status != ItemStatus.Open)
        {
            _items.Reopen(row.Item.Id);
            Refresh();
        }
    }

    [RelayCommand]
    private void Edit(MemoRow row)
    {
        if (_dialogs.EditItem(row.Item, _settings.TimeZone))
        {
            Refresh();
        }
    }

    [RelayCommand]
    private void Delete(MemoRow row)
    {
        if (_dialogs.Confirm($"删除备忘“{row.Content}”？"))
        {
            _items.Delete(row.Item.Id);
            Refresh();
        }
    }

    private static string BuildMeta(
        Item item,
        DateTimeOffset? reminderAt,
        TimeZoneInfo timeZone,
        DateTimeOffset now)
    {
        var parts = new List<string>();
        if (reminderAt is { } at)
        {
            parts.Add(at <= now
                ? $"提醒 {RowFormat.ReminderTime(at, timeZone)} · 已到期"
                : $"提醒 {RowFormat.ReminderTime(at, timeZone)}");
        }
        else
        {
            parts.Add($"录入 {BusinessTime.FormatLocal(item.CreatedAtUtc, timeZone, "MM-dd HH:mm")}");
        }

        if (item.MemoDate is { } memoDate)
        {
            parts.Add($"备忘 {memoDate:MM-dd}");
        }
        else if (item.ItemDate is { } itemDate)
        {
            parts.Add($"日期 {itemDate:MM-dd}");
        }
        else if (item.WorkTime is { } workTime)
        {
            parts.Add($"发生 {workTime.Date:MM-dd}");
        }

        if (item.Tags.Count > 0)
        {
            parts.Add(string.Join(" · ", item.Tags.Select(tag => $"#{tag}")));
        }

        return string.Join("　", parts);
    }

    private static string StatusText(Item item) => item.Status switch
    {
        ItemStatus.Completed => "已完成",
        ItemStatus.Archived => "已归档",
        _ => string.Empty,
    };

    private string? BuildParseText(Item item)
    {
        if (item.CaptureId is not { } captureId)
        {
            return null;
        }

        return _parseResults.GetLatestForCapture(captureId)?.Status switch
        {
            MemoParseStatus.Parsing => "整理中",
            MemoParseStatus.AwaitingApply => "待应用整理",
            MemoParseStatus.Failed or MemoParseStatus.Skipped => "已按原文保存",
            _ => null,
        };
    }

    private bool CanRetryParse(Item item)
    {
        if (item.CaptureId is not { } captureId || item.Status != ItemStatus.Open)
        {
            return false;
        }

        return _parseResults.GetLatestForCapture(captureId)?.Status is
            MemoParseStatus.Failed or MemoParseStatus.Skipped;
    }
}
