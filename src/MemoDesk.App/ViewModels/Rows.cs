using MemoDesk.Core.Domain;
using MemoDesk.Core.Time;
using MemoDesk.Core.Timeline;
using MemoDesk.Infrastructure.Queries;

namespace MemoDesk.App.ViewModels;

public sealed record TodoRow(Item Item, string Content, string Meta, string StatusText, bool IsOpen);

public sealed record MemoRow(Item Item, string Content, string Meta, string StatusText, bool IsOpen, bool CanRetry = false)
    : System.ComponentModel.INotifyPropertyChanged
{
    private string _meta = Meta;
    public string Meta { get => _meta; set { if (_meta == value) return; _meta = value; PropertyChanged?.Invoke(this, new(nameof(Meta))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed record WorkLogRow(
    Item Item,
    string TimeText,
    string Content,
    string RawText,
    string RecordedText,
    string Note = "");

public sealed record TimelineRow(TimelineEntry Entry, string TimeText, string TypeText, string Content);

public sealed record SearchRow(SearchHit Hit, string TypeText, string TimeText, string StatusText, string Snippet)
{
    public Item Item => Hit.Item;
}

public static class RowFormat
{
    public static string TypeText(ItemType type) => type switch
    {
        ItemType.Todo => "待办",
        ItemType.WorkLog => "工作记录",
        ItemType.Memo => "备忘",
        _ => string.Empty,
    };

    public static string StatusText(Item item) => item.Type switch
    {
        ItemType.Todo => item.Status == ItemStatus.Completed ? "已完成" : "未完成",
        ItemType.Memo => item.Status switch
        {
            ItemStatus.Completed => "已完成",
            ItemStatus.Archived => "已归档",
            _ => "未处理",
        },
        _ => string.Empty,
    };

    public static string Local(DateTimeOffset utc, TimeZoneInfo timeZone, string format = "MM-dd HH:mm") =>
        BusinessTime.FormatLocal(utc, timeZone, format);

    public static string ReminderTime(DateTimeOffset utc, TimeZoneInfo timeZone) =>
        BusinessTime.FormatLocal(utc, timeZone, utc.Second == 0 ? "MM-dd HH:mm" : "MM-dd HH:mm:ss");

    public static string BuildTodoMeta(Item item, TimeZoneInfo timeZone)
    {
        var parts = new List<string>();

        if (item.ItemDate is { } date)
        {
            parts.Add($"日期 {date:MM-dd}");
        }

        if (item.DueAtUtc is { } due)
        {
            parts.Add($"截止 {Local(due, timeZone)}");
        }

        if (item.Priority is { } priority)
        {
            parts.Add(priority switch
            {
                ItemPriority.High => "高优先级",
                ItemPriority.Low => "低优先级",
                _ => "中优先级",
            });
        }

        if (item.Tags.Count > 0)
        {
            parts.Add(string.Join(" · ", item.Tags.Select(tag => $"#{tag}")));
        }

        return parts.Count == 0 ? "未安排" : string.Join("　", parts);
    }

    public static string BuildWorkTimeText(Item item, TimeZoneInfo timeZone)
    {
        var workTime = item.WorkTime;
        if (workTime is null)
        {
            return string.Empty;
        }

        var dateText = workTime.Date.ToString("MM-dd");
        var endDateText = (workTime.EndDate ?? workTime.Date).ToString("MM-dd");

        return workTime.Precision switch
        {
            WorkTimePrecision.Exact when workTime.Start is { } start =>
                $"{dateText} {start:HH\\:mm}",
            WorkTimePrecision.Range when workTime.Start is { } rangeStart && workTime.End is { } rangeEnd =>
                $"{dateText} {rangeStart:HH\\:mm} – {endDateText} {rangeEnd:HH\\:mm}",
            WorkTimePrecision.Period =>
                $"{dateText} {workTime.OriginalText ?? "时段"}" + (workTime.Start is { } s ? $"（{s:HH\\:mm} 起）" : string.Empty),
            _ => $"{dateText} · 时间未细分",
        };
    }
}
