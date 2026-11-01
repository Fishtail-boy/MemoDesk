using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using MemoDesk.App.ViewModels;
using MemoDesk.Platform.Windows.Input;
using MemoDesk.Vault.Core;

namespace MemoDesk.App.Views;

public sealed class VaultQuickWindow : VaultDialogWindow
{
    private readonly PasswordBox _master;
    private readonly TextBox _query;
    private readonly ListBox _rows = new() { Height = 240, Margin = new Thickness(0, 10, 0, 10) };
    private readonly StackPanel _locked = new();
    private readonly StackPanel _search = new();
    private IReadOnlyList<VaultEntrySummary> _all = [];
    public VaultQuickWindow(VaultViewModel model) : base(model, "快捷检索密码", 570, 560)
    {
        Note("Enter 复制密码 · Ctrl+Enter 复制用户名 · Esc 关闭");
        _master = PasswordField("主密码", maxLength: 0);
        Body.Children.Remove(_master); _locked.Children.Add(_master);
        var unlock = ActionButton("解锁并检索", Unlock_Click, primary: true);
        Body.Children.Remove(unlock); _locked.Children.Add(unlock); Body.Children.Add(_locked);
        _query = Field("搜索名称、用户名、网址或标签");
        Body.Children.Remove(_query); _search.Children.Add(_query); _search.Children.Add(_rows);
        var factory = new FrameworkElementFactory(typeof(StackPanel));
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new Binding("Title"));
        var user = new FrameworkElementFactory(typeof(TextBlock)); user.SetBinding(TextBlock.TextProperty, new Binding("UserName"));
        user.SetValue(TextBlock.FontSizeProperty, 11.0); factory.AppendChild(title); factory.AppendChild(user);
        _rows.ItemTemplate = new DataTemplate { VisualTree = factory };
        var password = ActionButton("复制密码", (_, _) => Copy(true), primary: true);
        Body.Children.Remove(password); _search.Children.Add(password);
        var username = ActionButton("复制用户名", (_, _) => Copy(false));
        Body.Children.Remove(username); _search.Children.Add(username); Body.Children.Add(_search); Body.Children.Add(Error);
        _query.TextChanged += (_, _) => ApplyFilter();
        _query.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !ImeComposition.IsComposing(_query)) { e.Handled = true; Copy((Keyboard.Modifiers & ModifierKeys.Control) == 0); }
            if (e.Key == Key.Down && _rows.Items.Count > 0) { _rows.SelectedIndex = Math.Min(_rows.Items.Count - 1, _rows.SelectedIndex + 1); e.Handled = true; }
        };
        _rows.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Copy((Keyboard.Modifiers & ModifierKeys.Control) == 0); } };
        _master.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Unlock_Click(this, e); } };
        _master.PasswordChanged += (_, _) => { Error.Text = ""; _master.SetResourceReference(BorderBrushProperty, "RuleBrush"); _master.BorderThickness = new Thickness(1); };
        Loaded += (_, _) => { Refresh(); if (Model.IsUnlocked) _query.Focus(); else _master.Focus(); };
        ClearOnClose(() => { _all = []; _rows.ItemsSource = null; });
    }
    private void Refresh()
    {
        _locked.Visibility = Model.Vault.IsUnlocked ? Visibility.Collapsed : Visibility.Visible;
        _search.Visibility = Model.Vault.IsUnlocked ? Visibility.Visible : Visibility.Collapsed;
        if (Model.Vault.IsUnlocked)
        {
            try { _all = Model.Vault.List(); ApplyFilter(); }
            catch (VaultException ex) { Error.Text = VaultViewModel.ErrorText(ex.Error); }
        }
    }
    private void ApplyFilter()
    {
        if (!Model.Vault.IsUnlocked) { _rows.ItemsSource = null; return; }
        _rows.ItemsSource = VaultSearch.Filter(_all, _query.Text); if (_rows.Items.Count > 0) _rows.SelectedIndex = 0;
    }
    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (Revoked || Model.IsBusy) return;
        var error = MasterPasswordPolicy.GetError(_master.Password, creating: false);
        if (error is not null)
        {
            Error.Text = error; _master.SetResourceReference(BorderBrushProperty, "DangerBrush");
            _master.BorderThickness = new Thickness(2); _master.Focus(); return;
        }
        var password = _master.Password; _master.Clear();
        var generation = Model.Vault.SessionVersion;
        if (await Model.RunAsync(() => Model.Vault.Unlock(password, generation)) && !Revoked)
        { Refresh(); _query.Focus(); } else Error.Text = Model.Status;
        password = "";
    }
    private void Copy(bool password)
    {
        if (Revoked || !Model.Vault.IsUnlocked || _rows.SelectedItem is not VaultEntrySummary row) return;
        if (Model.TryCopyEntry(row, password)) Close(); else Error.Text = Model.Status;
    }
}
