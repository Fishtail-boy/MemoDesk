using System.Windows;
using MemoDesk.App.Services;
using MemoDesk.App.ViewModels;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Time;
using System.Windows.Threading;
using MemoDesk.Platform.Windows.Windowing;

namespace MemoDesk.App.Views;

public partial class InAppReminderWindow : Window
{
    private readonly DispatcherTimer _dismiss = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset _lastInteraction = DateTimeOffset.UtcNow;
    public InAppReminderWindow(PendingReminder pending, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(pending);

        InitializeComponent();
        MemoDesk.Platform.Windows.Windowing.WindowEffects.Attach(this, transient: true, passive: true);

        TypeAndTime.Text = $"{RowFormat.TypeText(pending.Item.Type)} · {BusinessTime.FormatLocal(pending.Occurrence.ScheduledAtUtc, timeZone, "MM-dd HH:mm:ss")}";
        ContentText.Text = pending.Item.Content;
        CatchUpBadge.Visibility = pending.IsRecovery ? Visibility.Visible : Visibility.Collapsed;

        SnoozeBox.ItemsSource = pending.Occurrence.OptionsSnapshot;
        SnoozeBox.SelectedIndex = 0;

        Loaded += (_, _) =>
        {
            var area = new MonitorLocator().GetWorkAreaForCursor();
            Left = area.Left + area.Width - ActualWidth - 20;
            Top = area.Top + area.Height - ActualHeight - 20;
            foreach (var widget in Application.Current.Windows.OfType<MemoWidgetWindow>().Where(w => w.IsVisible))
            {
                if (new Rect(Left, Top, ActualWidth, ActualHeight).IntersectsWith(new Rect(widget.Left, widget.Top, widget.ActualWidth, widget.ActualHeight)))
                {
                    if (widget.Top - ActualHeight - 12 >= area.Top)
                        Top = widget.Top - ActualHeight - 12;
                    else if (widget.Left - ActualWidth - 12 >= area.Left)
                        Left = widget.Left - ActualWidth - 12;
                    else
                        Top = Math.Min(area.Top + area.Height - ActualHeight - 12, widget.Top + widget.ActualHeight + 12);
                }
            }
            _dismiss.Start();
        };
        _dismiss.Tick += (_, _) =>
        {
            if (IsMouseOver || IsKeyboardFocusWithin || SnoozeBox.IsDropDownOpen) _lastInteraction = DateTimeOffset.UtcNow;
            else if (DateTimeOffset.UtcNow - _lastInteraction >= TimeSpan.FromSeconds(12)) Close();
        };
        Closed += (_, _) => _dismiss.Stop();
    }

    public void SetQueueCount(int count)
    {
        QueueHint.Text = count > 0 ? $"还有 {count} 条提醒" : "关闭后仍保留为未完成";
        NextButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public event Action? CompleteRequested;

    public event Action<string>? SnoozeRequested;

    private void Complete_Click(object sender, RoutedEventArgs e) => CompleteRequested?.Invoke();

    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        if (SnoozeBox.SelectedItem is SnoozeOption option)
        {
            SnoozeRequested?.Invoke(option.Id);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
