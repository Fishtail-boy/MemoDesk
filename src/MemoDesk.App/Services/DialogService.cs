using System.Windows;
using MemoDesk.App.Views;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Core.Reminders;
using MemoDesk.Infrastructure.Settings;

namespace MemoDesk.App.Services;

/// <summary>应用内对话框与事项编辑入口，避免页面直接创建窗口。</summary>
public sealed class DialogService
{
    private readonly ItemService _items;
    private readonly IReminderRepository _reminders;
    private readonly TimeProvider _clock;
    private readonly AppSettings? _settings;

    public DialogService(ItemService items, IReminderRepository reminders, TimeProvider clock, AppSettings? settings = null)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _settings = settings;
    }

    /// <summary>编辑事项并把内容与提醒计划一起保存。返回是否发生了修改。</summary>
    public bool EditItem(Item item, TimeZoneInfo timeZone)
    {
        var activeReminder = _reminders.GetActiveForItem(item.Id);
        var window = new ItemEditWindow(
            item,
            timeZone,
            activeReminder?.ReminderAtUtc,
            activeReminder,
            request => _items.UpdateWithReminder(request.Item, request.Reminder),
            _settings?.SnoozeOptions)
        {
            Owner = Application.Current?.MainWindow,
        };

        return window.ShowDialog() == true;
    }

    public bool Confirm(string message, string title = "备忘助手")
    {
        var owner = Application.Current?.MainWindow;
        var result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    public void ShowError(string message) =>
        MessageBox.Show(
            Application.Current?.MainWindow,
            message,
            "备忘助手",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
}
