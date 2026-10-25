using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoDesk.App.Services;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Core.Time;
using MemoDesk.Core.Timeline;
using MemoDesk.Infrastructure.Queries;
using MemoDesk.Infrastructure.Settings;

namespace MemoDesk.App.ViewModels;

/// <summary>今日概览（设计 2.3）。</summary>
public partial class TodayViewModel : ObservableObject
{
    private readonly ItemQueryService _query;
    private readonly ItemService _items;
    private readonly AppSettings _settings;
    private readonly TimeProvider _clock;

    public TodayViewModel(ItemQueryService query, ItemService items, AppSettings settings, TimeProvider clock)
    {
        _query = query;
        _items = items;
        _settings = settings;
        _clock = clock;
    }

    public ObservableCollection<TodoRow> TodayTodos { get; } = new();

    public ObservableCollection<TodoRow> OverdueTodos { get; } = new();

    public ObservableCollection<TimelineRow> Timeline { get; } = new();

    [ObservableProperty]
    private string todayText = string.Empty;

    [ObservableProperty]
    private string dateDay = string.Empty;

    [ObservableProperty]
    private string dateMeta = string.Empty;

    [ObservableProperty]
    private string ledeText = string.Empty;

    [ObservableProperty]
    private int unscheduledCount;

    [ObservableProperty]
    private int memoCount;

    [ObservableProperty]
    private int pendingDraftCount;

    [ObservableProperty]
    private bool isTimelineEmpty = true;

    [ObservableProperty]
    private bool hasTodayTodos;

    [ObservableProperty]
    private bool hasOverdue;

    [ObservableProperty]
    private bool hasReminderInfo;

    public event Action<string>? NavigateRequested;

    public event Action? QuickCaptureRequested;

    public void Refresh()
    {
        var timeZone = _settings.TimeZone;
        var now = _clock.GetUtcNow();
        var today = BusinessTime.Today(now, timeZone);
        var snapshot = _query.GetTodaySnapshot(today, now, timeZone);

        TodayText = $"{today:yyyy年M月d日} · {WeekdayText(today)}";
        DateDay = today.ToString("MM.dd", CultureInfo.InvariantCulture);
        DateMeta = $"{WeekdayText(today)} · {ChineseYear(today.Year)}";

        TodayTodos.Clear();
        foreach (var item in snapshot.TodayTodos)
        {
            TodayTodos.Add(new TodoRow(
                item,
                item.Content,
                RowFormat.BuildTodoMeta(item, timeZone),
                RowFormat.StatusText(item),
                item.Status == ItemStatus.Open));
        }

        OverdueTodos.Clear();
        foreach (var item in snapshot.OverdueTodos)
        {
            OverdueTodos.Add(new TodoRow(
                item,
                item.Content,
                RowFormat.BuildTodoMeta(item, timeZone),
                RowFormat.StatusText(item),
                item.Status == ItemStatus.Open));
        }

        Timeline.Clear();
        foreach (var entry in snapshot.Timeline)
        {
            var typeText = entry.EventType == TimelineEventType.WorkOccurred
                ? "工作记录"
                : entry.ItemType switch
                {
                    ItemType.Todo => "完成待办",
                    ItemType.Memo => "完成备忘",
                    _ => "完成",
                };

            Timeline.Add(new TimelineRow(
                entry,
                entry.DisplayTime,
                typeText,
                entry.Content));
        }

        UnscheduledCount = snapshot.UnscheduledCount;
        MemoCount = snapshot.UnprocessedMemoCount;
        IsTimelineEmpty = Timeline.Count == 0;
        HasTodayTodos = TodayTodos.Count > 0;
        HasOverdue = OverdueTodos.Count > 0;

        LedeText = TodayTodos.Count > 0 || OverdueTodos.Count > 0
            ? "今天先把手头的几件事安放好，剩下的交给记录。"
            : "今天没有必须完成的事，留一点空白给意外。";
    }

    [RelayCommand]
    private void Complete(TodoRow row)
    {
        if (!row.IsOpen)
        {
            return;
        }

        _items.Complete(row.Item.Id);
        Refresh();
    }

    [RelayCommand]
    private void Navigate(string key) => NavigateRequested?.Invoke(key);

    [RelayCommand]
    private void QuickCapture() => QuickCaptureRequested?.Invoke();

    private static string WeekdayText(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => "星期一",
        DayOfWeek.Tuesday => "星期二",
        DayOfWeek.Wednesday => "星期三",
        DayOfWeek.Thursday => "星期四",
        DayOfWeek.Friday => "星期五",
        DayOfWeek.Saturday => "星期六",
        _ => "星期日",
    };

    private static string ChineseYear(int year) => new(
        year.ToString(CultureInfo.InvariantCulture)
            .Select(digit => "〇一二三四五六七八九"[digit - '0'])
            .ToArray());
}
