using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MemoDesk.App.Services;
using MemoDesk.Infrastructure.Settings;
using MemoDesk.Vault.Core;

namespace MemoDesk.App.ViewModels;

/// <summary>只缓存元信息，密码从不进入属性绑定、通知、备忘或 AI。</summary>
public sealed partial class VaultViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private IReadOnlyList<VaultEntrySummary> _all = [];
    public IVaultService Vault { get; }
    public ISecureClipboard Clipboard { get; }
    public VaultLockCoordinator Locks { get; }
    public AppSettings Settings { get; }
    public VaultHotkeyManager Hotkeys { get; }
    public VaultViewModel(IVaultService vault, ISecureClipboard clipboard, VaultLockCoordinator locks, AppSettings settings, VaultHotkeyManager hotkeys)
    {
        Vault = vault; Clipboard = clipboard; Locks = locks; Settings = settings; Hotkeys = hotkeys;
        vault.Locked += OnLocked; UpdateState();
    }
    public ObservableCollection<VaultEntrySummary> Rows { get; } = [];
    public ObservableCollection<string> Filters { get; } = ["全部", "收藏", "回收站", "工作", "个人"];
    public event Action? ClearSensitiveRequested;
    [ObservableProperty] private bool isUnlocked;
    [ObservableProperty] private bool isNew;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string query = "";
    [ObservableProperty] private string filter = "全部";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private VaultEntrySummary? selectedEntry;
    public bool HasSelection => SelectedEntry is not null;
    public string UnlockLabel => IsNew ? "创建密码库" : "解锁密码库";
    public string BackupStatus => Vault.BackupFailed ? "自动备份失败，下次保存将重试；建议手动导出备份。" :
        Vault.LastBackupPath is null ? "请定期将加密备份保存到其他位置。" : "最近备份：" + System.IO.Path.GetFileName(Vault.LastBackupPath);
    public void Touch() => Locks.Activity.Touch();
    private void UpdateState()
    {
        IsUnlocked = Vault.IsUnlocked; IsNew = !Vault.Exists;
        OnPropertyChanged(nameof(UnlockLabel)); OnPropertyChanged(nameof(BackupStatus));
    }
    partial void OnQueryChanged(string value) { Touch(); ApplyFilter(); }
    partial void OnFilterChanged(string value) { Touch(); ApplyFilter(); }
    partial void OnSelectedEntryChanged(VaultEntrySummary? value)
    { ClearSensitiveRequested?.Invoke(); OnPropertyChanged(nameof(HasSelection)); }
    private void ApplyFilter()
    {
        var selected = SelectedEntry?.Id;
        SelectedEntry = null; Rows.Clear();
        foreach (var row in VaultSearch.Filter(_all, Query, Filter is "收藏" or "回收站" ? "全部" : Filter, Filter == "收藏", Filter == "回收站")) Rows.Add(row);
        SelectedEntry = Rows.FirstOrDefault(r => r.Id == selected);
    }
    private void ApplyRows(IReadOnlyList<VaultEntrySummary> rows)
    {
        _all = rows;
        var current = Filter;
        Filters.Clear();
        foreach (var f in new[] { "全部", "收藏", "回收站", "工作", "个人" }.Concat(rows.Select(r => r.Category)).Distinct()) Filters.Add(f);
        Filter = Filters.Contains(current) ? current : "全部"; ApplyFilter();
    }
    public void Refresh() { if (!IsBusy) _ = RunAsync(() => { }, "", touch: false); }
    public async Task<bool> RunAsync(Action operation, string success = "", bool touch = true, bool changesSession = false)
    {
        if (IsBusy) return false;
        if (touch) Touch();
        IsBusy = true; Status = "";
        var version = Vault.SessionVersion;
        try
        {
            var rows = await Task.Run(() =>
            {
                if (version != Vault.SessionVersion) throw new VaultException(VaultError.Cancelled);
                operation(); return Vault.IsUnlocked ? Vault.List() : null;
            });
            if (version != Vault.SessionVersion && !changesSession) { if (Vault.IsUnlocked) Locks.Lock(); return false; }
            UpdateState(); if (rows is not null) ApplyRows(rows);
            Status = success; return true;
        }
        catch (VaultException ex) { if (version == Vault.SessionVersion) Status = ErrorText(ex.Error); return false; }
        catch (Exception) { if (version == Vault.SessionVersion) Status = "操作失败，请检查文件权限或稍后重试。"; return false; }
        finally { IsBusy = false; UpdateState(); }
    }
    public void ClearVisibleDetails() { SelectedEntry = null; ClearSensitiveRequested?.Invoke(); }
    public void ReportError(VaultError error) => Status = ErrorText(error);
    public bool TryCopyEntry(VaultEntrySummary entry, bool password)
    {
        var version = Vault.SessionVersion;
        if (!Vault.IsUnlocked || entry.IsDeleted) return false;
        try
        {
            using var secret = password ? Vault.ReadSecret(entry.Id, entry.RowVersion) : null;
            if (version != Vault.SessionVersion || !Vault.IsUnlocked) return false;
            var copied = Clipboard.Copy(password ? secret!.Password : entry.UserName);
            if (version != Vault.SessionVersion) { Clipboard.ClearOwned(); return false; }
            Status = copied ? "已复制，将在 20 秒后清除。" : "复制失败，请重试。";
            return copied;
        }
        catch (VaultException ex) { ReportError(ex.Error); return false; }
    }
    public static string ErrorText(VaultError error) => error switch
    {
        VaultError.Locked or VaultError.Cancelled => "密码库已锁定，请重新解锁。",
        VaultError.InvalidPassword => "主密码不正确，或密码库密钥数据损坏。请检查输入后重试。",
        VaultError.InvalidPasswordFormat => "主密码格式不符合要求：" + MasterPasswordPolicy.Requirements,
        VaultError.Conflict => "条目已被修改，请刷新后重试。",
        VaultError.AlreadyExists => "密码库已经存在，请解锁。",
        VaultError.TooLarge => "文件、字段或条目数量超过限制。",
        VaultError.InvalidMapping => "请选择名称和密码列；密码、备注列不能同时用于名称、用户名、网址或分类。",
        VaultError.NotFound => "条目不存在或已进入回收站。",
        VaultError.Storage => "无法读写密码库，请检查磁盘空间和文件权限。",
        _ => "数据格式无效或校验失败，原文件已保留。"
    };
    private void OnLocked()
    {
        void Clear()
        {
            _all = []; SelectedEntry = null; Rows.Clear();
            Query = ""; Filter = "全部"; Filters.Clear();
            foreach (var f in new[] { "全部", "收藏", "回收站", "工作", "个人" }) Filters.Add(f);
            Status = ""; UpdateState(); ClearSensitiveRequested?.Invoke();
        }
        if (_dispatcher.CheckAccess()) Clear(); else _dispatcher.BeginInvoke(Clear);
    }
}
