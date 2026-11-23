using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MemoDesk.App.Services;
using MemoDesk.App.ViewModels;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Time;
using MemoDesk.Infrastructure.Queries;
using MemoDesk.Infrastructure.Settings;
using MemoDesk.Infrastructure.Storage;
using MemoDesk.Platform.Windows.Notifications;
using MemoDesk.Platform.Windows.Windowing;
using Microsoft.Data.Sqlite;

namespace MemoDesk.App.Views;

public partial class MemoWidgetWindow : Window
{
    private static readonly string[] Titles = ["备忘", "数字资产", "密码库", "日程", "消息", "额度"];
    private const int AssetsPage = 1;
    private const int VaultPage = 2;
    private const int QuotaPage = 5;
    private readonly ItemQueryService _query;
    private readonly ReminderQueryService? _reminderQuery;
    private readonly ItemService _items;
    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private readonly TimeProvider _clock;
    private readonly IReminderRepository? _reminders;
    private readonly SqliteDigitalAssetStore _assets;
    private readonly SettingsViewModel _preferences;
    private readonly MonitorLocator _monitors = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _leave = new() { Interval = TimeSpan.FromMilliseconds(360) };
    private readonly Dictionary<Guid, DateTimeOffset?> _plans = new();
    private readonly HashSet<DateTimeOffset> _announcedResets = new();
    private readonly CancellationTokenSource _lifetime = new();
    private WidgetState _state;
    private WorkArea _area;
    private TimeZoneInfo _zone = TimeZoneInfo.Local;
    private DateOnly _currentDate;
    private int _page;
    private Point? _swipeStart;
    private int _dialogDepth;
    private int _overdue;
    private DateTimeOffset? _nextDue;
    private bool _expanded;
    private bool _suppressHover;
    private bool _initialized;
    private bool _loadingQuota;
    private CodexQuota? _codex;
    private DateTimeOffset? _lastQuotaFetch;
    private DateTimeOffset? _quotaCheckedAt;
    private WeChatFlashListener? _wechat;
    private IReadOnlyList<ReminderOverviewRow> _agenda = [];
    private DigitalAssetRow[] _assetRows = [];
    private DateTimeOffset _nextAssetRefresh;

    public MemoWidgetWindow(ItemQueryService query, ItemService items, AppSettings settings,
        DialogService dialogs, TimeProvider clock, SqliteDigitalAssetStore assets, SettingsViewModel preferences, IReminderRepository? reminders = null,
        ReminderQueryService? reminderQuery = null)
    {
        _query = query;
        _items = items;
        _settings = settings;
        _dialogs = dialogs;
        _clock = clock;
        _assets = assets;
        _preferences = preferences;
        _reminders = reminders;
        _reminderQuery = reminderQuery;
        _state = settings.Widget;

        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            WindowEffects.SetPassive(this, true);
            ConfigureWeChat();
        };
        Deactivated += (_, _) => WindowEffects.SetPassive(this, true);
        _tick.Tick += (_, _) => UpdateStatus();
        _hover.Tick += (_, _) =>
        {
            _hover.Stop();
            if (IsMouseOver && !_suppressHover && !_expanded) SetExpanded(true, animate: true);
        };
        _leave.Tick += (_, _) =>
        {
            _leave.Stop();
            if (!IsMouseOver && !HasOpenPopup() && _dialogDepth == 0 && !_state.KeepOpen)
                SetExpanded(false, animate: true);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible || _state.TrackQuota) _tick.Start();
            else _tick.Stop();
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Closed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            _lifetime.Cancel();
            _lifetime.Dispose();
            _wechat?.Dispose();
            _hover.Stop();
            _leave.Stop();
            _tick.Stop();
        };

        if (_state.TrackQuota) _tick.Start();
    }

    public event Action? VisibilityChanged;
    public event Action? ItemsChanged;
    public event Action? OpenMainRequested;
    public event Action<string>? OpenPageRequested;
    public event Action<Rect>? CaptureRequested;
    public event Action<string>? QuotaReset;

    private string Dock => _state.Dock is "left" or "right" ? _state.Dock : "top";

    public void ShowWidget()
    {
        if (IsVisible)
        {
            Refresh();
            return;
        }

        var state = _settings.Widget;
        var workAreas = _monitors.GetWorkAreas();
        var target = workAreas.FirstOrDefault(area => state.Left is { } left && state.Top is { } top
            && left >= area.Left && left < area.Left + area.Width
            && top >= area.Top && top < area.Top + area.Height);
        _area = target.Width > 0 ? target : _monitors.GetWorkAreaForCursor();
        _state = state with { Visible = true, Pinned = state.Dock is null || state.Pinned };
        Topmost = _state.Pinned;
        _suppressHover = false;
        SetExpanded(_state.KeepOpen, animate: false);
        _initialized = true;
        Show();
        Refresh();
        if (!state.Visible) SaveState();
        VisibilityChanged?.Invoke();
    }

    public void HideWidget()
    {
        if (!IsVisible) return;
        _hover.Stop();
        _leave.Stop();
        IslandMenu.IsOpen = false;
        Hide();
        _state = _state with { Visible = false };
        SaveState();
        VisibilityChanged?.Invoke();
    }

    public void ToggleVisibility()
    {
        if (IsVisible) HideWidget();
        else ShowWidget();
    }

    public void Refresh()
    {
        _zone = _settings.TimeZone;
        var previousDay = _currentDate;
        _currentDate = BusinessTime.Today(_clock.GetUtcNow(), _zone);
        _plans.Clear();
        var rows = _query.GetWidgetMemos(_currentDate, includePrevious: !_state.TodayOnly)
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => new MemoRow(item, item.Content, "", RowFormat.StatusText(item), item.Status == ItemStatus.Open))
            .ToArray();
        MemoList.ItemsSource = rows;
        OpenCount.Text = rows.Length.ToString();
        DateText.Text = _state.TodayOnly ? "今日待处理" : "待处理";
        EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        _agenda = (_reminderQuery?.GetOverview() ?? []).Where(row => row.PlanEnabled).OrderBy(row => row.PlanAtUtc).ToArray();
        var activeTimes = _agenda.ToDictionary(row => row.ItemId, row => row.PlanAtUtc);
        foreach (var row in rows)
        {
            _plans[row.Item.Id] = _reminderQuery is null
                ? _reminders?.GetActiveForItem(row.Item.Id)?.ReminderAtUtc
                : activeTimes.TryGetValue(row.Item.Id, out var at) ? at : null;
        }
        if (AgendaCalendar.SelectedDate is not { } selected || DateOnly.FromDateTime(selected) == previousDay)
            AgendaCalendar.SelectedDate = _currentDate.ToDateTime(TimeOnly.MinValue);
        RefreshAgenda();
        RefreshOverdue(_clock.GetUtcNow());
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var now = _clock.GetUtcNow();
        if (IsVisible)
        {
            var local = TimeZoneInfo.ConvertTime(now, _zone);
            if (_currentDate != default && DateOnly.FromDateTime(local.DateTime) != _currentDate)
            {
                Refresh();
                return;
            }

            CompactClock.Text = local.ToString("HH:mm");
            ExpandedClock.Text = local.ToString("MM-dd   HH:mm");
            if (_nextDue is { } nextDue && now >= nextDue) RefreshOverdue(now);
            RefreshCountdowns(now);
            UpdateCompactStatus();
            if (_expanded && _page == AssetsPage && now >= _nextAssetRefresh)
                RefreshAssets(now);
        }
        if (_state.TrackQuota && (_lastQuotaFetch is not { } fetched || now - fetched >= TimeSpan.FromMinutes(5)))
            _ = RefreshQuotaAsync();
        CheckQuotaResets(now);
    }

    private void RefreshOverdue(DateTimeOffset now)
    {
        var due = _agenda.TakeWhile(row => row.PlanAtUtc <= now).ToArray();
        _overdue = due.Length;
        _nextDue = _agenda.Skip(due.Length).FirstOrDefault()?.PlanAtUtc;
        MessageRows.ItemsSource = FormatReminders(due);
        MessagesEmpty.Visibility = due.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshAgenda()
    {
        if (AgendaCalendar.SelectedDate is not { } selected) return;
        var rows = _agenda.Where(row => TimeZoneInfo.ConvertTime(row.PlanAtUtc, _zone).Date == selected.Date).ToArray();
        AgendaDateText.Text = $"{selected:MM 月 dd 日} · {rows.Length} 条提醒";
        AgendaRows.ItemsSource = FormatReminders(rows);
        AgendaEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IslandReminder[] FormatReminders(IEnumerable<ReminderOverviewRow> rows)
    {
        return rows.Select(row => new IslandReminder(row.ItemId,
            BusinessTime.FormatLocal(row.PlanAtUtc, _zone, row.PlanAtUtc.Second == 0 ? "HH:mm" : "HH:mm:ss"),
            row.Content)).ToArray();
    }

    private sealed record IslandReminder(Guid Id, string Time, string Content);
    private sealed record IslandQuotaRow(string Usage, string Reset, double Remaining);

    private void SetExpanded(bool expanded, bool animate)
    {
        _expanded = expanded;
        _swipeStart = null;
        if (expanded) ShowPage(_page);
        CompactBar.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        PlaceIsland();
        if (expanded && animate && SystemParameters.ClientAreaAnimation)
        {
            ExpandedPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop,
                });
            ExpandedPanel.RenderTransform = new TranslateTransform();
            var axis = Dock == "top" ? TranslateTransform.YProperty : TranslateTransform.XProperty;
            var distance = Dock == "right" ? 6 : -6;
            ((TranslateTransform)ExpandedPanel.RenderTransform).BeginAnimation(axis,
                new DoubleAnimation(distance, 0, TimeSpan.FromMilliseconds(170)) { FillBehavior = FillBehavior.Stop });
        }
    }

    private void PlaceIsland()
    {
        var side = Dock != "top";
        var width = Math.Min(_area.Width, _expanded ? 600 : side ? 50 : 154);
        var height = Math.Min(_area.Height, _expanded ? 330 : side ? 114 : 36);
        Width = width;
        Height = height;
        Left = Dock switch
        {
            "left" => _area.Left + 6,
            "right" => _area.Left + _area.Width - width - 6,
            _ => _area.Left + (_area.Width - width) / 2,
        };
        Top = side ? _area.Top + (_area.Height - height) / 2 : _area.Top + 6;
        Island.CornerRadius = new CornerRadius(_expanded ? 25 : side ? 23 : 18);
        CompactContents.Orientation = side ? Orientation.Vertical : Orientation.Horizontal;
        CompactStatus.Width = side ? 42 : 64;
        CompactStatus.Margin = side ? new Thickness(0, 6, 0, 0) : new Thickness(4, 0, 0, 0);
    }

    private void SaveState()
    {
        if (!_initialized) return;
        _state = _state with { Left = _area.Left, Top = _area.Top, Dock = Dock };
        _settings.Widget = _state;
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.InvokeAsync(() =>
        {
            if (!IsVisible) return;
            var areas = _monitors.GetWorkAreas();
            if (areas.Contains(_area)) return;
            _area = areas.FirstOrDefault(area => area.Left == _area.Left && area.Top == _area.Top);
            if (_area.Width <= 0) _area = _monitors.GetWorkAreaForCursor();
            PlaceIsland();
            SaveState();
        });
    }

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _leave.Stop();
        if (!_expanded && !_suppressHover) _hover.Start();
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        _suppressHover = false;
        _hover.Stop();
        QueueCollapse();
    }

    private void QueueCollapse()
    {
        if (_expanded && !IsMouseOver && !_state.KeepOpen && _dialogDepth == 0 && !HasOpenPopup())
            _leave.Start();
    }

    private bool HasOpenPopup() => IslandMenu.IsOpen || Mouse.Captured is ContextMenu;

    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        _hover.Stop();
        SetExpanded(true, animate: true);
        WindowEffects.Focus(this);
        ((Button)PageTabs.Children[_page]).Focus();
    }

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        _suppressHover = true;
        _state = _state with { KeepOpen = false };
        SetExpanded(false, animate: false);
        if (IsActive) CompactButton.Focus();
        SaveState();
    }

    private void Island_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_expanded || HasOpenPopup()) return;
        Collapse_Click(sender, e);
        e.Handled = true;
    }

    private void ShowPage(int index)
    {
        PreferencesPanel.Visibility = Visibility.Collapsed;
        _page = index;
        PanelTitle.Text = Titles[index];
        FrameworkElement[] pages = [MemosPanel, AssetsPanel, VaultPanel, AgendaPanel, MessagesPanel, QuotaPanel];
        for (var i = 0; i < pages.Length; i++)
        {
            pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            var tab = (Button)PageTabs.Children[i];
            tab.SetResourceReference(ForegroundProperty, i == index ? "AccentBrush" : "MutedBrush");
            tab.SetResourceReference(BackgroundProperty, i == index ? "AccentSoftBrush" : "PaperBrush");
        }

        if (index == QuotaPage && (_lastQuotaFetch is not { } last || _clock.GetUtcNow() - last >= TimeSpan.FromMinutes(5)))
            _ = RefreshQuotaAsync();
        if (index == AssetsPage)
            RefreshAssets(_clock.GetUtcNow());
    }

    public void ShowPreferences()
    {
        ShowWidget();
        SetExpanded(true, animate: false);
        _preferences.Load();
        PreferencesPanel.Content = new IslandPreferencesView { DataContext = _preferences };
        foreach (FrameworkElement panel in new FrameworkElement[] { MemosPanel, AssetsPanel, VaultPanel, AgendaPanel, MessagesPanel, QuotaPanel })
            panel.Visibility = Visibility.Collapsed;
        PreferencesPanel.Visibility = Visibility.Visible;
        PanelTitle.Text = "灵动岛设置";
    }

    public void ShowVaultModule()
    {
        ShowWidget();
        _leave.Stop();
        _page = VaultPage;
        SetExpanded(true, animate: false);
    }

    private void RefreshAssets(DateTimeOffset now)
    {
        _nextAssetRefresh = now.AddSeconds(5);
        try
        {
            var zone = _settings.TimeZone;
            var today = BusinessTime.Today(now, zone);
            var rows = _assets.List().Select(asset =>
                new DigitalAssetRow(asset, asset.GetExpirationStatus(now, zone), today)).ToArray();
            if (!_assetRows.SequenceEqual(rows))
            {
                _assetRows = rows;
                AssetRows.ItemsSource = rows;
            }
            AssetsStatus.Text = rows.Length == 0 ? "还没有数字资产。" : string.Empty;
        }
        catch (SqliteException)
        {
            AssetsStatus.Text = "读取数字资产失败，正在重试。";
        }
    }

    private void OpenAssetLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DigitalAssetRow { Asset.Link: { } link } } && !DigitalAssetLinks.Open(link))
            AssetsStatus.Text = "无法打开资产链接。";
    }

    private void PageTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var page)) ShowPage(page);
    }

    private void PageStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var delta))
            StepPage(delta);
    }

    private void StepPage(int delta) => ShowPage((_page + delta + Titles.Length) % Titles.Length);

    private void PageBody_MouseDown(object sender, MouseButtonEventArgs e) =>
        _swipeStart = e.GetPosition(PageBody);

    private void PageBody_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var start = _swipeStart;
        _swipeStart = null;
        if (start is null)
            return;

        var delta = e.GetPosition(PageBody) - start.Value;
        if (Math.Abs(delta.X) < 40 || Math.Abs(delta.X) <= Math.Abs(delta.Y) * 1.5)
            return;

        // Buttons may own the press; releasing here cancels their click once a swipe is recognized.
        if (Mouse.Captured is UIElement captured && PageBody.IsAncestorOf(captured))
            Mouse.Capture(null);
        e.Handled = true;
        StepPage(delta.X < 0 ? 1 : -1);
    }

    private void AgendaDate_Changed(object sender, SelectionChangedEventArgs e) => RefreshAgenda();

    private void Dock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string dock }) return;
        _state = _state with { Dock = dock };
        PlaceIsland();
        SaveState();
    }

    private void MoveToDisplay_Click(object sender, RoutedEventArgs e)
    {
        _area = _monitors.GetWorkAreaForCursor();
        PlaceIsland();
        SaveState();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = PinMenuItem.IsChecked;
        _state = _state with { Pinned = Topmost };
        SaveState();
    }

    private void KeepOpen_Click(object sender, RoutedEventArgs e)
    {
        _state = _state with { KeepOpen = KeepOpenMenuItem.IsChecked };
        if (_state.KeepOpen) SetExpanded(true, animate: true);
        else QueueCollapse();
        SaveState();
    }

    private void TodayOnly_Click(object sender, RoutedEventArgs e)
    {
        _state = _state with { TodayOnly = TodayMenuItem.IsChecked };
        SaveState();
        Refresh();
    }

    private void IslandMenu_Opening(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenIslandMenu(CompactButton);
    }

    private void OpenIslandMenu(FrameworkElement anchor)
    {
        TopDock.IsChecked = Dock == "top";
        LeftDock.IsChecked = Dock == "left";
        RightDock.IsChecked = Dock == "right";
        PinMenuItem.IsChecked = Topmost;
        KeepOpenMenuItem.IsChecked = _state.KeepOpen;
        TodayMenuItem.IsChecked = _state.TodayOnly;
        WeChatMenuItem.IsChecked = _state.WeChatAlerts;
        QuotaMenuItem.IsChecked = _state.TrackQuota;
        IslandMenu.PlacementTarget = anchor;
        IslandMenu.IsOpen = true;
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, MenuButton)) OpenIslandMenu(MenuButton);
        else if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void Menu_Closed(object sender, RoutedEventArgs e) => QueueCollapse();

    private void Hide_Click(object sender, RoutedEventArgs e) => HideWidget();

    private void Capture_Click(object sender, RoutedEventArgs e) =>
        CaptureRequested?.Invoke(new Rect(Left, Top, ActualWidth, ActualHeight));

    private void OpenMain_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string page } && OpenPageRequested is { } request) request(page);
        else OpenMainRequested?.Invoke();
    }

    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MemoRow row) return;
        _items.Complete(row.Item.Id);
        NotifyItemsChanged();
    }

    private void EditReminder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is IslandReminder row && _items.Get(row.Id) is { } item) EditItem(item);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MemoRow row) EditItem(row.Item);
    }

    private void EditItem(Item item) => RunDialog(() => _dialogs.EditItem(item, _settings.TimeZone, this));

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MemoRow row)
            RunDialog(() =>
            {
                if (!_dialogs.Confirm($"删除备忘“{row.Content}”？", owner: this)) return false;
                _items.Delete(row.Item.Id);
                return true;
            });
    }

    private void RunDialog(Func<bool> dialog)
    {
        _dialogDepth++;
        try
        {
            if (dialog()) NotifyItemsChanged();
        }
        finally
        {
            _dialogDepth--;
            QueueCollapse();
        }
    }

    private void Archive_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MemoRow row) return;
        _items.Archive(row.Item.Id);
        NotifyItemsChanged();
    }

    private void NotifyItemsChanged()
    {
        Refresh();
        ItemsChanged?.Invoke();
    }

    private void RefreshCountdowns(DateTimeOffset now)
    {
        if (MemoList.ItemsSource is not IEnumerable<MemoRow> rows) return;
        foreach (var row in rows)
        {
            if (_plans.TryGetValue(row.Item.Id, out var plan) && plan is { } reminderAt)
            {
                var left = reminderAt - now;
                var target = BusinessTime.FormatLocal(reminderAt, _zone,
                    reminderAt.Second == 0 ? "MM-dd HH:mm" : "MM-dd HH:mm:ss");
                row.Meta = left > TimeSpan.Zero && left <= TimeSpan.FromMinutes(1)
                    ? $"剩余 {Math.Ceiling(left.TotalSeconds):0} 秒 · {target}"
                    : left <= TimeSpan.Zero ? $"提醒已到 · {target}" : target;
            }
            else row.Meta = row.Item.MemoDate is { } date && date < _currentDate ? $"{date:MM-dd} · 未完成" : "";
        }
    }

    private void ConfigureWeChat()
    {
        _wechat?.Dispose();
        _wechat = null;
        if (_state.WeChatAlerts && HwndSource.FromHwnd(new WindowInteropHelper(this).Handle) is { } source)
        {
            _wechat = new WeChatFlashListener(source);
            _wechat.Changed += UpdateWeChat;
        }
        UpdateWeChat();
    }

    private void UpdateWeChat()
    {
        WeChatStatus.Text = _wechat switch
        {
            { IsRegistered: false } => "微信 · Windows 任务栏提醒不可用",
            { Count: > 0 } => $"微信 · {_wechat.Count} 个窗口有新消息",
            not null => "微信 · 等待任务栏提醒",
            _ => "微信任务栏提醒已关闭",
        };
        WeChatButton.Content = _state.WeChatAlerts ? "打开消息" : "启用";
        WeChatButton.IsEnabled = !_state.WeChatAlerts || _wechat is { Count: > 0 };
        UpdateCompactStatus();
    }

    private void WeChatToggle_Click(object sender, RoutedEventArgs e)
    {
        _state = _state with { WeChatAlerts = WeChatMenuItem.IsChecked };
        ConfigureWeChat();
        SaveState();
    }

    private void WeChat_Click(object sender, RoutedEventArgs e)
    {
        if (!_state.WeChatAlerts)
        {
            WeChatMenuItem.IsChecked = true;
            WeChatToggle_Click(sender, e);
            return;
        }
        if (_wechat?.OpenLatest() == false) WeChatStatus.Text = "请从 Windows 任务栏打开微信。";
    }

    private void UpdateCompactStatus()
    {
        var count = (_wechat?.Count ?? 0) + _overdue;
        var reset = _state.TrackQuota && _announcedResets.Count > 0;
        var now = _clock.GetUtcNow();
        var next = _state.TrackQuota ? _codex?.Windows.Where(w => w.ResetsAt > now).MinBy(w => w.ResetsAt) : null;
        CompactStatus.Text = count > 0 ? $"消息 {count}" : reset ? "已重置"
            : next is not null ? $"{next.Remaining:0}%" : "备忘";
        CompactButton.ToolTip = next?.ResetsAt is { } at
            ? $"Codex {next.Label} · {BusinessTime.FormatLocal(at, _zone, "MM-dd HH:mm")} 重置"
            : null;
        CompactIcon.Text = count > 0 ? "●" : "◉";
        CompactIcon.SetResourceReference(ForegroundProperty, count > 0 ? "DangerBrush" : "AccentBrush");
        CompactStatus.SetResourceReference(ForegroundProperty, count > 0 ? "DangerBrush" : reset ? "AccentBrush" : "FaintBrush");
    }

    private void QuotaToggle_Click(object sender, RoutedEventArgs e)
    {
        _state = _state with { TrackQuota = QuotaMenuItem.IsChecked };
        _quotaCheckedAt = _clock.GetUtcNow();
        _announcedResets.Clear();
        SaveState();
        if (_state.TrackQuota) _ = RefreshQuotaAsync();
    }

    private void RefreshQuota_Click(object sender, RoutedEventArgs e) => _ = RefreshQuotaAsync();

    private async Task RefreshQuotaAsync()
    {
        if (_loadingQuota || _lifetime.IsCancellationRequested) return;
        _loadingQuota = true;
        _lastQuotaFetch = _clock.GetUtcNow();
        RefreshQuotaButton.IsEnabled = false;
        CodexStatus.Text = DeepSeekStatus.Text = "查询中…";
        try
        {
            var token = _lifetime.Token;
            var codex = IslandQuotaService.ReadCodexAsync(token);
            var deepSeek = IslandQuotaService.ReadDeepSeekAsync(_settings, token);
            await Task.WhenAll(codex, deepSeek);
            var now = _clock.GetUtcNow();
            CheckQuotaResets(now);
            _codex = await codex;
            var zone = _settings.TimeZone;
            CodexStatus.Text = _codex.Windows.Count == 0 ? _codex.Status : $"更新于 {BusinessTime.FormatLocal(now, zone, "HH:mm")}";
            QuotaRows.ItemsSource = _codex.Windows.Select(window => new IslandQuotaRow(
                $"{window.Label} · 剩余 {window.Remaining:0}%",
                window.ResetsAt is not { } time ? "重置时间未知"
                    : time <= now ? "重置时间已到，等待更新"
                    : $"{BusinessTime.FormatLocal(time, zone, "MM-dd HH:mm")} 重置",
                window.Remaining)).ToArray();
            DeepSeekStatus.Text = await deepSeek;
        }
        catch (OperationCanceledException) { }
        finally
        {
            _loadingQuota = false;
            RefreshQuotaButton.IsEnabled = true;
        }
    }

    private void CheckQuotaResets(DateTimeOffset now)
    {
        if (!_state.TrackQuota) return;
        var previous = _quotaCheckedAt ?? now;
        _quotaCheckedAt = now;
        _announcedResets.RemoveWhere(at => now - at >= TimeSpan.FromMinutes(2));
        if (_codex?.Windows is not { Count: > 0 } windows) return;
        var due = windows.Where(w => w.ResetsAt is { } at && at > previous && at <= now
            && !_announcedResets.Contains(at)).ToArray();
        if (due.Length == 0) return;
        foreach (var window in due) _announcedResets.Add(window.ResetsAt!.Value);
        QuotaReset?.Invoke($"Codex {string.Join("、", due.Select(window => window.Label))}额度已到重置时间。");
        if (!_loadingQuota) _ = RefreshQuotaAsync();
    }
}
