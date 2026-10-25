using System.Windows;
using MemoDesk.Core.Drafting;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Time;

namespace MemoDesk.App.Views;

public sealed record ItemEditSaveRequest(Item Item, ReminderEditIntent Reminder);

/// <summary>编辑事项的内容、日期与提醒。保存只返回修改结果，持久化由 DialogService 负责。</summary>
public partial class ItemEditWindow : Window
{
    private readonly Item _original;
    private readonly TimeZoneInfo _timeZone;
    private readonly DateTimeOffset? _reminderAtUtc;
    private readonly ReminderPlan? _activeReminder;
    private readonly Func<ItemEditSaveRequest, MemoEditSaveResult>? _save;
    private DateTime? _originalReminderDate;
    private string _originalReminderTimeText = string.Empty;

    public ItemEditWindow(
        Item item,
        TimeZoneInfo timeZone,
        DateTimeOffset? reminderAtUtc,
        ReminderPlan? activeReminder = null,
        Func<ItemEditSaveRequest, MemoEditSaveResult>? save = null,
        IReadOnlyList<SnoozeOption>? quickOptions = null)
    {
        _original = item ?? throw new ArgumentNullException(nameof(item));
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        _reminderAtUtc = reminderAtUtc;
        _activeReminder = activeReminder;
        _save = save;

        InitializeComponent();
        MemoDesk.Platform.Windows.Windowing.WindowEffects.Attach(this, transient: true);
        RemindTimeBox.TimeZone = timeZone;
        RemindTimeBox.QuickOptions = quickOptions ?? SnoozeOption.Defaults;
        RemindTimeBox.TargetRequested += target =>
        {
            var local = TimeZoneInfo.ConvertTime(target, _timeZone);
            RemindDatePicker.SelectedDate = local.Date;
            RemindTimeBox.Text = local.ToString(local.Second == 0 ? "HH:mm" : "HH:mm:ss");
        };
        Populate();
    }

    public Item? Result { get; private set; }

    public DateTimeOffset? ResultReminderAtUtc { get; private set; }

    private void Populate()
    {
        TypeText.Text = _original.Type switch
        {
            ItemType.Todo => "待办",
            ItemType.WorkLog => "工作记录",
            _ => "备忘",
        };

        ContentBox.Text = _original.Content;
        TagsBox.Text = string.Join(", ", _original.Tags);
        PriorityBox.SelectedIndex = _original.Priority switch
        {
            ItemPriority.Low => 1,
            ItemPriority.Normal => 2,
            ItemPriority.High => 3,
            _ => 0,
        };

        TodoPanel.Visibility = _original.Type == ItemType.Todo ? Visibility.Visible : Visibility.Collapsed;
        MemoPanel.Visibility = _original.Type == ItemType.Memo ? Visibility.Visible : Visibility.Collapsed;
        WorkPanel.Visibility = _original.Type == ItemType.WorkLog || _original.WorkTime is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReminderPanel.Visibility = _original.Type != ItemType.WorkLog
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_original.ItemDate is { } itemDate)
        {
            ItemDatePicker.SelectedDate = itemDate.ToDateTime(TimeOnly.MinValue);
        }

        if (_original.MemoDate is { } memoDate)
        {
            MemoDatePicker.SelectedDate = memoDate.ToDateTime(TimeOnly.MinValue);
        }

        if (_original.WorkTime is { } workTime)
        {
            WorkDatePicker.SelectedDate = workTime.Date.ToDateTime(TimeOnly.MinValue);
            WorkStartBox.Text = workTime.Start?.ToString("HH:mm") ?? string.Empty;
            WorkEndBox.Text = workTime.End?.ToString("HH:mm") ?? string.Empty;
            WorkEndDatePicker.SelectedDate = workTime.EndDate?.ToDateTime(TimeOnly.MinValue);
        }

        if (_reminderAtUtc is { } remindAt)
        {
            var local = TimeZoneInfo.ConvertTime(remindAt, _timeZone);
            RemindDatePicker.SelectedDate = local.Date;
            RemindTimeBox.Text = local.ToString(local.Second == 0 ? "HH:mm" : "HH:mm:ss");
        }

        _originalReminderDate = RemindDatePicker.SelectedDate;
        _originalReminderTimeText = RemindTimeBox.Text;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateDates())
        {
            return;
        }
        foreach (var field in new[] { RemindTimeBox, WorkStartBox, WorkEndBox })
        {
            field.CommitText();
            if (!field.IsValid) { ShowError(field.ValidationError!); return; }
        }

        var content = ContentBox.Text.Trim();
        if (content.Length == 0)
        {
            ShowError("内容不能为空。");
            return;
        }

        var updated = _original with
        {
            Content = content,
            Tags = TagsBox.Text
                .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToArray(),
            Priority = PriorityBox.SelectedIndex switch
            {
                1 => ItemPriority.Low,
                2 => ItemPriority.Normal,
                3 => ItemPriority.High,
                _ => null,
            },
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        switch (_original.Type)
        {
            case ItemType.Todo:
                updated = updated with
                {
                    ItemDate = ItemDatePicker.SelectedDate is { } date ? DateOnly.FromDateTime(date) : null,
                };
                break;
            case ItemType.Memo:
                updated = updated with
                {
                    MemoDate = MemoDatePicker.SelectedDate is { } memoDate
                        ? DateOnly.FromDateTime(memoDate)
                        : _original.MemoDate,
                };
                break;
        }

        if (WorkPanel.Visibility == Visibility.Visible)
        {
            var withWorkTime = BuildWorkLog(updated);
            if (withWorkTime is null)
            {
                return;
            }

            updated = withWorkTime;
        }

        DateTimeOffset? reminder = null;
        var hasReminderDate = RemindDatePicker.SelectedDate is not null;
        var hasReminderTime = !string.IsNullOrWhiteSpace(RemindTimeBox.Text);
        if (hasReminderDate != hasReminderTime)
        {
            ShowError("提醒日期和时间必须同时填写，或清除提醒。");
            return;
        }

        if (hasReminderDate)
        {
            if (RemindTimeBox.SelectedTime is not { } time)
            {
                ShowError("提醒时间格式无效，应为 HH:mm 或 HH:mm:ss。");
                return;
            }

            var remindDate = DateOnly.FromDateTime(RemindDatePicker.SelectedDate!.Value);
            reminder = BusinessTime.Combine(remindDate, time, _timeZone);
        }

        // 已完成/已归档事项不因编辑重新启用提醒。
        if (_original.Status != ItemStatus.Open)
        {
            reminder = null;
        }

        var reminderKind = _activeReminder is null
            ? (reminder is null ? ReminderEditKind.Unchanged : ReminderEditKind.Set)
            : (reminder is not null
                && RemindDatePicker.SelectedDate == _originalReminderDate
                && string.Equals(RemindTimeBox.Text.Trim(), _originalReminderTimeText, StringComparison.Ordinal)
                ? ReminderEditKind.Unchanged
                : reminder is null ? ReminderEditKind.Clear : ReminderEditKind.Set);

        if (reminderKind == ReminderEditKind.Set && reminder <= DateTimeOffset.UtcNow)
        { ShowError("提醒时间已过去，请选择未来时间。需立即提醒可设置几秒后的快捷时间。"); return; }

        var intent = new ReminderEditIntent(
            reminderKind,
            reminder,
            _activeReminder?.Id,
            _activeReminder?.PlanVersion,
            _activeReminder is not null);

        if (_save is not null)
        {
            var result = _save(new ItemEditSaveRequest(updated, intent));
            if (!result.IsSuccess)
            {
                ShowError(result.Error ?? "保存失败，请重试。");
                return;
            }
        }

        Result = updated;
        ResultReminderAtUtc = reminder;
        DialogResult = true;
    }

    private void ClearReminder_Click(object sender, RoutedEventArgs e)
    {
        RemindDatePicker.SelectedDate = null;
        RemindTimeBox.Text = string.Empty;
    }

    private Item? BuildWorkLog(Item updated)
    {
        var date = WorkDatePicker.SelectedDate is { } selected
            ? DateOnly.FromDateTime(selected)
            : (DateOnly?)null;
        var fallbackDate = _original.WorkTime?.Date ?? DateOnly.FromDateTime(DateTime.Today);
        var endDate = WorkEndDatePicker.SelectedDate is { } selectedEnd
            ? DateOnly.FromDateTime(selectedEnd)
            : (DateOnly?)null;

        var result = WorkTimeRules.Build(
            _original.WorkTime,
            date,
            WorkStartBox.Text,
            WorkEndBox.Text,
            endDate,
            fallbackDate);

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            ShowError(result.Error);
            return null;
        }

        return updated with { WorkTime = result.WorkTime };
    }

    private bool ValidateDates()
    {
        foreach (var field in new[] { ItemDatePicker, MemoDatePicker, WorkDatePicker, WorkEndDatePicker, RemindDatePicker })
        {
            field.CommitText();
            if (!field.IsValid)
            {
                ShowError(field.ValidationError ?? "日期输入无效，请修改后再保存。");
                return false;
            }
        }

        return true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
