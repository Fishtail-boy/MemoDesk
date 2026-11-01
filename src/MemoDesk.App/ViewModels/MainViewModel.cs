using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MemoDesk.App.ViewModels;

/// <summary>顶部索引标签。</summary>
public sealed partial class NavItemViewModel : ObservableObject
{
    public NavItemViewModel(string key, string title)
    {
        Key = key;
        Title = title;
    }

    public string Key { get; }

    public string Title { get; }

    [ObservableProperty]
    private bool isActive;

    [ObservableProperty]
    private bool hasBadge;
}

/// <summary>主窗口导航与页面生命周期。</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly TodayViewModel _today;
    private readonly DraftsViewModel _drafts;
    private readonly TodosViewModel _todos;
    private readonly RemindersViewModel _reminders;
    private readonly WorkLogsViewModel _workLogs;
    private readonly MemosViewModel _memos;
    private readonly SearchViewModel _search;
    private readonly SettingsViewModel _settings;
    private readonly VaultViewModel? _vault;

    public MainViewModel(
        TodayViewModel today,
        DraftsViewModel drafts,
        TodosViewModel todos,
        RemindersViewModel reminders,
        WorkLogsViewModel workLogs,
        MemosViewModel memos,
        SearchViewModel search,
        SettingsViewModel settings,
        VaultViewModel? vault = null)
    {
        _today = today;
        _drafts = drafts;
        _todos = todos;
        _reminders = reminders;
        _workLogs = workLogs;
        _memos = memos;
        _search = search;
        _settings = settings;
        _vault = vault;

        Navigation = new[]
        {
            new NavItemViewModel("memos", "备忘"),
            new NavItemViewModel("vault", "密码库"),
            new NavItemViewModel("worklogs", "工作记录"),
            new NavItemViewModel("reminders", "提醒"),
            new NavItemViewModel("settings", "设置"),
        };

        _drafts.DraftCountChanged += OnDraftCountChanged;
        _today.NavigateRequested += NavigateTo;

        NavigateTo("memos");
        _drafts.Refresh();
    }

    public IReadOnlyList<NavItemViewModel> Navigation { get; }

    public event Action<int>? PendingDraftCountChanged;

    [ObservableProperty]
    private object? currentPage;

    [ObservableProperty]
    private string currentKey = "memos";

    [ObservableProperty]
    private int pendingDraftCount;

    [RelayCommand]
    private void Navigate(string key) => NavigateTo(key);

    public void NavigateTo(string key)
    {
        if (CurrentKey == "vault" && key != "vault") _vault?.ClearVisibleDetails();
        if (CurrentKey == "drafts" && key != "drafts")
        {
            _drafts.PersistEdits();
        }

        object page;
        switch (key)
        {
            case "vault" when _vault is not null:
                _vault.Refresh();
                page = _vault;
                break;
            case "drafts":
                _drafts.Refresh();
                page = _drafts;
                break;
            case "todos":
                _todos.Refresh();
                page = _todos;
                break;
            case "reminders":
                _reminders.Refresh();
                page = _reminders;
                break;
            case "worklogs":
                _workLogs.Refresh();
                page = _workLogs;
                break;
            case "settings":
                _settings.Load();
                page = _settings;
                break;
            case "search":
                page = _search;
                break;
            default:
                _memos.Refresh();
                key = "memos";
                page = _memos;
                break;
        }

        CurrentKey = key;
        CurrentPage = page;
        foreach (var item in Navigation)
        {
            item.IsActive = item.Key == key;
        }
    }

    /// <summary>外部数据发生变化（确认草稿、提醒完成等）后刷新当前页。</summary>
    public void RefreshCurrent()
    {
        switch (CurrentKey)
        {
            case "vault":
                break; // 备忘后台刷新不能重置密码库选择或延长会话。
            case "drafts":
                _drafts.Refresh();
                break;
            case "todos":
                _todos.Refresh();
                break;
            case "reminders":
                _reminders.Refresh();
                break;
            case "worklogs":
                _workLogs.Refresh();
                break;
            case "search":
                _search.Refresh();
                break;
            case "settings":
                _settings.Load();
                break;
            default:
                _memos.Refresh();
                break;
        }
    }

    public void RefreshDraftsAndToday()
    {
        _drafts.Refresh();
        _today.Refresh();
    }

    /// <summary>后台整理完成后刷新备忘、工作记录、提醒和当前页。</summary>
    public void RefreshAfterMemoChange()
    {
        _memos.Refresh();
        _workLogs.Refresh();
        _reminders.Refresh();
        RefreshCurrent();
    }

    private void OnDraftCountChanged(object? sender, int count)
    {
        PendingDraftCount = count;
        _today.PendingDraftCount = count;
        PendingDraftCountChanged?.Invoke(count);
    }
}
