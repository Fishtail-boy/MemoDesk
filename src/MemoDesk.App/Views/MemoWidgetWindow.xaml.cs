using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MemoDesk.App.Services;
using MemoDesk.App.ViewModels;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Core.Time;
using MemoDesk.Infrastructure.Queries;
using MemoDesk.Infrastructure.Settings;
using MemoDesk.Core.Reminders;
using MemoDesk.Platform.Windows.Windowing;
using System.Windows.Controls;

namespace MemoDesk.App.Views;

/// <summary>轻量桌面便签：默认保留今日与往日未完成内容，可直接完成/编辑/删除。</summary>
public partial class MemoWidgetWindow : Window
{
    private readonly ItemQueryService _query;
    private readonly ItemService _items;
    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private readonly TimeProvider _clock;

    private readonly DispatcherTimer _dateTimer;
    private readonly DispatcherTimer _stateSaveTimer;
    private DateOnly _currentDate;
    private bool _restoring;
    private readonly IReminderRepository? _reminders;
    private bool _locked;
    private bool _collapsed;
    private bool _todayOnly;
    private double _expandedHeight = 260;
    private readonly Dictionary<Guid, ReminderPlan?> _plans = new();
    private readonly Dictionary<Guid, MemoRow> _completing = new();

    public MemoWidgetWindow(
        ItemQueryService query,
        ItemService items,
        AppSettings settings,
        DialogService dialogs,
        TimeProvider clock,
        IReminderRepository? reminders = null)
    {
        _query = query;
        _items = items;
        _settings = settings;
        _dialogs = dialogs;
        _clock = clock;
        _reminders = reminders;

        InitializeComponent();
        WindowEffects.Attach(this, passive: true);

        _dateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dateTimer.Tick += (_, _) => { RefreshIfDayChanged(); RefreshCountdowns(); };

        _stateSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _stateSaveTimer.Tick += (_, _) =>
        {
            _stateSaveTimer.Stop();
            SaveState(IsVisible);
        };

        Loaded += OnLoaded;
        Closing += OnClosing;
        LocationChanged += (_, _) => ScheduleStateSave();
        SizeChanged += (_, _) => ScheduleStateSave();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Closed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            _dateTimer.Stop(); _stateSaveTimer.Stop();
        };
    }

    public event Action? VisibilityChanged;

    public event Action? ItemsChanged;

    public event Action? OpenMainRequested;

    public void ShowWidget()
    {
        Show();
        Refresh();
        SaveState(visible: true);
        VisibilityChanged?.Invoke();
    }

    public void HideWidget()
    {
        Hide();
        SaveState(visible: false);
        VisibilityChanged?.Invoke();
    }

    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            HideWidget();
        }
        else
        {
            ShowWidget();
        }
    }

    public void Refresh()
    {
        var timeZone = _settings.TimeZone;
        _currentDate = BusinessTime.Today(_clock.GetUtcNow(), timeZone);

        _plans.Clear();
        var rows = _query.GetWidgetMemos(_currentDate, includePrevious: !_todayOnly)
            .Select(item => new MemoRow(
                item,
                item.Content,
                item.MemoDate is { } date ? $"备忘日期 {date:MM-dd}" : "无日期",
                RowFormat.StatusText(item),
                item.Status == ItemStatus.Open))
            .ToList();
        foreach (var row in rows) _plans[row.Item.Id] = _reminders?.GetActiveForItem(row.Item.Id);
        rows.AddRange(_completing.Values.Where(row => rows.All(r => r.Item.Id != row.Item.Id)));
        rows = rows.OrderBy(row => row.Item.CreatedAtUtc).ToList();

        MemoList.ItemsSource = rows;
        DateText.Text = $"{(_todayOnly ? "今日" : "当前")} · {rows.Count(r => r.IsOpen)} 条";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshCountdowns();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreState();
        Refresh();
        _dateTimer.Start();
    }

    private void RestoreState()
    {
        _restoring = true;
        try
        {
            var state = _settings.Widget;
            _locked = state.Locked; _collapsed = state.Collapsed; _todayOnly = state.TodayOnly;
            LockMenuItem.IsChecked = _locked; CollapseMenuItem.IsChecked = _collapsed; TodayMenuItem.IsChecked = _todayOnly;
            Width = state.Width is > 200 ? state.Width.Value : Width;
            Height = state.Height is > 200 ? state.Height.Value : Height;
            _expandedHeight = Height;
            ApplyCollapsed();

            if (state.Left is { } left && state.Top is { } top)
            {
                Left = left;
                Top = top;
                EnsureOnScreen();
            }
            else
            {
                var area = SystemParameters.WorkArea;
                Left = area.Right - Width - 24;
                Top = area.Bottom - Height - 24;
            }

            Topmost = state.Pinned;
            PinButton.Content = state.Pinned ? "取消置顶" : "置顶";
        }
        finally
        {
            _restoring = false;
        }
    }

    private void EnsureOnScreen()
    {
        var locator = new MemoDesk.Platform.Windows.Windowing.MonitorLocator();
        var header = new Rect(Left, Top, Width, Math.Min(Height, 60));
        if (locator.GetWorkAreas().Any(area =>
        {
            var visible = Rect.Intersect(header, new Rect(area.Left, area.Top, area.Width, area.Height));
            return visible.Width >= 100 && visible.Height >= 40;
        })) return;
        var current = locator.GetWorkAreaForCursor();
        Left = Math.Max(current.Left + 12, current.Left + current.Width - Width - 24);
        Top = Math.Max(current.Top + 12, current.Top + current.Height - Height - 24);
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.InvokeAsync(() => { EnsureOnScreen(); SaveState(IsVisible); });
    }

    private void RefreshIfDayChanged()
    {
        var today = BusinessTime.Today(_clock.GetUtcNow(), _settings.TimeZone);
        if (today != _currentDate)
        {
            Refresh();
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_locked && e.ButtonState == MouseButtonState.Pressed && e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标状态变化导致 DragMove 失败，忽略。
            }
        }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinButton.Content = Topmost ? "取消置顶" : "置顶";
        SaveState(IsVisible);
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => HideWidget();

    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MemoRow row)
        {
            _completing[row.Item.Id] = row with { IsOpen = false };
            if (!_items.Complete(row.Item.Id)) _completing.Remove(row.Item.Id);
            Refresh();
            ItemsChanged?.Invoke();
            var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            delay.Tick += (_, _) => { delay.Stop(); _completing.Remove(row.Item.Id); Refresh(); };
            delay.Start();
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MemoRow row
            && _dialogs.EditItem(row.Item, _settings.TimeZone))
        {
            Refresh();
            ItemsChanged?.Invoke();
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MemoRow row
            && _dialogs.Confirm($"删除备忘“{row.Content}”？"))
        {
            _items.Delete(row.Item.Id);
            Refresh();
            ItemsChanged?.Invoke();
        }
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e) => OpenMainRequested?.Invoke();

    private void More_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { ContextMenu: { } menu } button) { menu.PlacementTarget = button; menu.IsOpen = true; } }
    private void Lock_Click(object sender, RoutedEventArgs e) { _locked = LockMenuItem.IsChecked; SaveState(IsVisible); }
    private void TodayOnly_Click(object sender, RoutedEventArgs e) { _todayOnly = TodayMenuItem.IsChecked; Refresh(); SaveState(IsVisible); }
    private void Collapse_Click(object sender, RoutedEventArgs e)
    { if (!_collapsed) _expandedHeight = Height; _collapsed = CollapseMenuItem.IsChecked; ApplyCollapsed(); SaveState(IsVisible); }
    private void ApplyCollapsed()
    {
        BodyPanel.Visibility = FooterPanel.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        Height = _collapsed ? 64 : _expandedHeight;
    }
    private void FocusWidget_Click(object sender, RoutedEventArgs e) => WindowEffects.Focus(this);
    private void Archive_Click(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is MemoRow row) { _items.Archive(row.Item.Id); Refresh(); ItemsChanged?.Invoke(); } }
    private void RefreshCountdowns()
    {
        if (MemoList.ItemsSource is not IEnumerable<MemoRow> rows) return;
        var now = _clock.GetUtcNow();
        foreach (var row in rows)
        {
            if (!row.IsOpen) { row.Meta = "已完成"; continue; }
            if (_plans.TryGetValue(row.Item.Id, out var plan) && plan is not null)
            {
                var left = plan.ReminderAtUtc - now;
                var target = Core.Time.BusinessTime.FormatLocal(plan.ReminderAtUtc, _settings.TimeZone,
                    plan.ReminderAtUtc.Second == 0 ? "MM-dd HH:mm" : "MM-dd HH:mm:ss");
                row.Meta = left > TimeSpan.Zero && left <= TimeSpan.FromMinutes(1)
                    ? $"剩余 {Math.Ceiling(left.TotalSeconds):0} 秒 · {target}"
                    : left <= TimeSpan.Zero ? $"提醒已到 · {target}" : target;
            }
            else row.Meta = row.Item.MemoDate is { } date && date < _currentDate ? $"{date:MM-dd} · 未完成" : string.Empty;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (Application.Current is App { IsExiting: true })
        {
            SaveState(IsVisible);
            return;
        }

        e.Cancel = true;
        HideWidget();
    }

    private void ScheduleStateSave()
    {
        if (_restoring)
        {
            return;
        }

        _stateSaveTimer.Stop();
        _stateSaveTimer.Start();
    }

    private void SaveState(bool visible)
    {
        if (_restoring)
        {
            return;
        }

        _settings.Widget = new WidgetState(
            Visible: visible,
            Pinned: Topmost,
            Left: Left,
            Top: Top,
            Width: Width,
            Height: _collapsed ? _expandedHeight : Height,
            Locked: _locked, Collapsed: _collapsed, TodayOnly: _todayOnly);
    }
}
