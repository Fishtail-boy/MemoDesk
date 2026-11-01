using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MemoDesk.App.ViewModels;
using MemoDesk.Vault.Core;
using Microsoft.Win32;

namespace MemoDesk.App.Views;

public partial class VaultView : UserControl
{
    private VaultViewModel? _model;
    private bool _validationShown, _resettingFields;
    private readonly DispatcherTimer _revealTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    public VaultView()
    {
        InitializeComponent();
        _revealTimer.Tick += (_, _) => ClearSecrets();
    }
    private VaultViewModel Vm => _model ??= (VaultViewModel)DataContext;
    private Window? OwnerWindow => Window.GetWindow(this);
    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        _model = (VaultViewModel)DataContext; _model.ClearSensitiveRequested += ClearAll;
        if (OwnerWindow is { } w) w.Deactivated += Deactivated;
        Vm.Refresh();
    }
    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_model is not null) _model.ClearSensitiveRequested -= ClearAll;
        if (OwnerWindow is { } w) w.Deactivated -= Deactivated;
        ClearAll(); // Keep the captured plain model available to already-authorized background operations.
    }
    private void Deactivated(object? sender, EventArgs e) => ClearSecrets();
    private void ClearSecrets() { RevealedPassword.Text = "••••••••••••"; RevealedNotes.Text = ""; _revealTimer.Stop(); }
    private void ClearAll()
    {
        _resettingFields = true;
        try { ClearSecrets(); MasterBox.Clear(); ConfirmMasterBox.Clear(); ClearFormError(); }
        finally { _resettingFields = false; }
    }
    private void ClearFormError()
    {
        _validationShown = false; MasterErrorText.Text = ""; MasterErrorPanel.Visibility = Visibility.Collapsed;
        MasterBox.Tag = ConfirmMasterBox.Tag = null;
    }
    private bool ValidateMasterForm(bool focus)
    {
        var error = MasterPasswordPolicy.GetError(MasterBox.Password, Vm.IsNew);
        var field = MasterBox;
        if (error is null && Vm.IsNew && MasterBox.Password != ConfirmMasterBox.Password)
        {
            error = ConfirmMasterBox.Password.Length == 0 ? "请再次输入主密码以确认。" : "两次输入的主密码不一致，请重新确认。";
            field = ConfirmMasterBox;
        }
        if (error is null) { ClearFormError(); return true; }
        ShowFormError(error, field, focus); return false;
    }
    private void ShowFormError(string error, PasswordBox field, bool focus)
    {
        _validationShown = true; MasterBox.Tag = ConfirmMasterBox.Tag = null; field.Tag = "Invalid";
        MasterErrorText.Text = error; MasterErrorPanel.Visibility = Visibility.Visible;
        if (focus) { field.Focus(); MasterErrorPanel.BringIntoView(); }
    }
    private void Master_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_validationShown && !_resettingFields) ValidateMasterForm(focus: false);
    }
    private void Activity(object sender, RoutedEventArgs e) { if (_model is not null) Vm.Touch(); }
    private void Master_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Unlock_Click(sender, e); } }
    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.IsBusy) return;
        Vm.Status = "";
        if (!ValidateMasterForm(focus: true)) return;
        var password = MasterBox.Password;
        var create = Vm.IsNew;
        var generation = Vm.Vault.SessionVersion;
        ClearAll();
        var success = await Vm.RunAsync(() => { if (create) Vm.Vault.Create(password, generation); else Vm.Vault.Unlock(password, generation); }, create ? "密码库已创建，建议导出一份加密备份。" : "");
        password = "";
        if (!success && generation == Vm.Vault.SessionVersion && IsLoaded && !Vm.IsUnlocked)
        { ShowFormError(Vm.Status, MasterBox, focus: true); Vm.Status = ""; }
    }
    private void Lock_Click(object sender, RoutedEventArgs e) => Vm.Locks.Lock();
    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (!Vm.IsUnlocked || Vm.IsBusy) return;
        new VaultEditWindow(Vm) { Owner = OwnerWindow }.ShowDialog();
    }
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedEntry is not { IsDeleted: false } row || Vm.IsBusy) return;
        try { new VaultEditWindow(Vm, row) { Owner = OwnerWindow }.ShowDialog(); }
        catch (VaultException ex) { Vm.ReportError(ex.Error); }
        catch (Exception) { Vm.Status = "无法打开账号，请刷新后重试。"; }
    }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedEntry is { IsDeleted: false } row && !Vm.IsBusy && Confirm("将这个账号移入回收站？"))
            await Vm.RunAsync(() => Vm.Vault.SetDeleted(row.Id, row.RowVersion, true), "已移入回收站。");
    }
    private async void RestoreEntry_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedEntry is { IsDeleted: true } row && !Vm.IsBusy)
            await Vm.RunAsync(() => Vm.Vault.SetDeleted(row.Id, row.RowVersion, false), "账号已恢复。");
    }
    private async void Purge_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedEntry is { IsDeleted: true } row && !Vm.IsBusy && Confirm("彻底删除这个账号？旧备份中仍可能保留历史副本。"))
            await Vm.RunAsync(() => Vm.Vault.DeletePermanently(row.Id, row.RowVersion), "账号已删除。");
    }
    private bool Confirm(string text) => MessageBox.Show(OwnerWindow!, text, "密码库", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void CopyUser_Click(object sender, RoutedEventArgs e) => Copy(secret: false);
    private void CopyPassword_Click(object sender, RoutedEventArgs e) => Copy(secret: true);
    private void Copy(bool secret)
    {
        if (Vm.SelectedEntry is not { IsDeleted: false } row || !Vm.IsUnlocked) return;
        Vm.TryCopyEntry(row, secret);
    }
    private void Reveal_Click(object sender, RoutedEventArgs e) => Reveal(notes: false);
    private void RevealNotes_Click(object sender, RoutedEventArgs e) => Reveal(notes: true);
    private void Reveal(bool notes)
    {
        if (Vm.SelectedEntry is not { IsDeleted: false } row || !Vm.IsUnlocked) return;
        try
        {
            using var secret = Vm.Vault.ReadSecret(row.Id, row.RowVersion);
            ClearSecrets();
            if (notes) RevealedNotes.Text = secret.Notes; else RevealedPassword.Text = secret.Password;
            _revealTimer.Start();
        }
        catch (VaultException ex) { Vm.ReportError(ex.Error); }
    }
    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.IsUnlocked && !Vm.IsBusy) new VaultGeneratorWindow(Vm) { Owner = OwnerWindow }.ShowDialog();
    }
    private void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!Vm.IsUnlocked || Vm.SelectedEntry is not { } row ||
            !Uri.TryCreate(row.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
        { Vm.Status = "请填写有效的 http / https 网址。"; return; }
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { Vm.Status = "无法打开网址。"; }
    }
    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (!Vm.IsUnlocked || Vm.IsBusy) return;
        var dialog = new SaveFileDialog { Filter = "MemoDesk 加密备份|*.memovault", FileName = $"MemoDesk-{DateTime.Now:yyyyMMdd}.memovault", AddExtension = true, DefaultExt = ".memovault" };
        if (dialog.ShowDialog(OwnerWindow) == true) await Vm.RunAsync(() => Vm.Vault.ExportBackup(dialog.FileName), "加密备份已导出。请妥善保管主密码。");
    }
    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.IsBusy) return;
        var dialog = new OpenFileDialog { Filter = "MemoDesk 加密备份|*.memovault" };
        if (dialog.ShowDialog(OwnerWindow) != true || !Confirm("恢复将替换当前密码库，原库会保留一份加密快照。继续？")) return;
        using var prompt = new VaultPasswordWindow("输入这份备份的主密码", Vm) { Owner = OwnerWindow };
        if (prompt.ShowDialog() == true)
        {
            var password = prompt.Password;
            var generation = Vm.Vault.SessionVersion;
            await Vm.RunAsync(() => Vm.Vault.RestoreBackup(dialog.FileName, password, generation), "备份已恢复，请使用这份备份的主密码解锁。", changesSession: true); password = "";
        }
    }
    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        if (!Vm.IsUnlocked || Vm.IsBusy) return;
        using var prompt = new VaultPasswordWindow("修改主密码", Vm, change: true) { Owner = OwnerWindow };
        if (prompt.ShowDialog() == true)
        {
            var current = prompt.CurrentPassword; var next = prompt.Password;
            if (await Vm.RunAsync(() => Vm.Vault.ChangeMasterPassword(current, next)))
            { Vm.Locks.Lock(); Vm.Status = "主密码已更新，请使用新密码解锁。旧备份仍使用旧密码。"; }
            current = next = "";
        }
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!Vm.IsUnlocked || Vm.IsBusy) return;
        var dialog = new OpenFileDialog { Filter = "CSV 文件|*.csv" };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        try { using var csv = VaultCsv.Read(dialog.FileName); new VaultImportWindow(Vm, csv) { Owner = OwnerWindow }.ShowDialog(); }
        catch (VaultException ex) { Vm.ReportError(ex.Error); }
        catch (Exception) { Vm.Status = "无法读取 CSV 文件，请检查文件格式和权限。"; }
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.IsUnlocked && !Vm.IsBusy) new VaultSettingsWindow(Vm) { Owner = OwnerWindow }.ShowDialog();
    }
}
