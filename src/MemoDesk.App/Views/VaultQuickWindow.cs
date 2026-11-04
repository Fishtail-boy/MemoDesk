using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using MemoDesk.App.ViewModels;
using MemoDesk.Platform.Windows.Input;
using MemoDesk.Vault.Core;

namespace MemoDesk.App.Views;

public sealed class VaultQuickWindow : VaultDialogWindow
{
    private readonly PasswordBox _master;
    private readonly TextBox _query;
    private readonly ListBox _rows = new() { Height = 220, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly StackPanel _locked = new();
    private readonly StackPanel _search = new();
    private readonly TextBlock _count = new(), _empty = new() { Text = "没有找到账号\n试试其他名称、用户名或网址", TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, LineHeight = 25 };
    private readonly TextBlock _keys = new();
    private readonly Button _copyPassword, _copyUser, _unlock;
    private IReadOnlyList<VaultEntrySummary> _all = [];
    public VaultQuickWindow(VaultViewModel model) : base(model, "快捷检索密码", 580, 580)
    {
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        Body.Children.Clear();
        Heading("密码库");
        Note("快速找到账号，按 Enter 复制密码。");
        // Keep labels inside their corresponding state so locked and unlocked UI never overlap.
        _master = PasswordField("主密码", maxLength: 0);
        var masterLabel = Body.Children[Body.Children.Count - 2]; Body.Children.Remove(masterLabel); Body.Children.Remove(_master);
        _locked.Children.Add(masterLabel); _locked.Children.Add(_master);
        _unlock = ActionButton("解锁并检索", Unlock_Click, primary: true);
        Body.Children.Remove(_unlock); _locked.Children.Add(_unlock);
        var lockHint = new TextBlock { Text = "输入主密码后检索你的账号", Margin = new Thickness(0, 18, 0, 0) };
        lockHint.SetResourceReference(StyleProperty, "MetaText"); _locked.Children.Add(lockHint); Body.Children.Add(_locked);
        _query = new TextBox { Height = 44, FontSize = 14, Margin = new Thickness(0, 0, 0, 12), ToolTip = "搜索名称、用户名、网址或标签" };
        _query.SetResourceReference(StyleProperty, "FieldBox"); ClearOnClose(_query.Clear);
        var searchBox = new Grid(); searchBox.Children.Add(_query);
        var placeholder = new TextBlock { Text = "搜索名称、用户名、网址或标签", Margin = new Thickness(12, 0, 12, 12),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "FaintBrush"); searchBox.Children.Add(placeholder);
        _query.TextChanged += (_, _) => placeholder.Visibility = _query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _search.Children.Add(searchBox);
        _count.SetResourceReference(StyleProperty, "MetaText"); _count.Margin = new Thickness(2, 0, 0, 8); _search.Children.Add(_count);
        _rows.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _rows.ItemContainerStyle = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBoxItem">
              <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
              <Setter Property="Padding" Value="12,10"/><Setter Property="Margin" Value="0,2"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
                <Border x:Name="Row" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" CornerRadius="8" Padding="{TemplateBinding Padding}" Background="Transparent">
                  <ContentPresenter/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource PaperBrush}"/></Trigger>
                  <Trigger Property="IsSelected" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource AccentSoftBrush}"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
        _rows.ItemTemplate = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <DockPanel LastChildFill="True">
                <Border DockPanel.Dock="Right" Background="{DynamicResource PaperBrush}" CornerRadius="5" Padding="8,3" Margin="12,0,0,0" VerticalAlignment="Center" MaxWidth="110">
                  <TextBlock Text="{Binding Category}" Foreground="{DynamicResource MutedBrush}" FontSize="11" TextTrimming="CharacterEllipsis"/>
                </Border>
                <StackPanel>
                  <TextBlock Text="{Binding Title}" Foreground="{DynamicResource InkBrush}" FontSize="14" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                  <TextBlock Text="{Binding UserName}" Foreground="{DynamicResource MutedBrush}" FontSize="12" Margin="0,4,0,0" TextTrimming="CharacterEllipsis"/>
                </StackPanel>
              </DockPanel>
            </DataTemplate>
            """);
        var list = new Grid(); list.Children.Add(_rows); list.Children.Add(_empty);
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var card = new Border { Child = list, CornerRadius = new CornerRadius(10), Padding = new Thickness(6), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "SheetBrush"); card.SetResourceReference(Border.BorderBrushProperty, "RuleBrush"); _search.Children.Add(card);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        _copyPassword = ActionButton("复制密码", (_, _) => Copy(true), primary: true); Body.Children.Remove(_copyPassword);
        _copyUser = ActionButton("复制用户名", (_, _) => Copy(false)); Body.Children.Remove(_copyUser); _copyUser.Margin = new Thickness(8, 8, 0, 0);
        actions.Children.Add(_copyPassword); actions.Children.Add(_copyUser); _search.Children.Add(actions);
        Body.Children.Add(_search); Body.Children.Add(Error);
        Footer.Visibility = Visibility.Visible;
        _keys.SetResourceReference(StyleProperty, "MetaText"); Footer.Children.Add(_keys);
        _rows.SelectionChanged += (_, _) => { UpdateActions(); if (_rows.SelectedItem is { } row) _rows.ScrollIntoView(row); };
        _query.TextChanged += (_, _) => ApplyFilter();
        _query.PreviewKeyDown += (_, e) =>
        {
            if (ImeComposition.IsComposing(_query)) return;
            if (e.Key == Key.Enter) { e.Handled = true; Copy((Keyboard.Modifiers & ModifierKeys.Control) == 0); }
            if (e.Key is Key.Down or Key.Up && _rows.Items.Count > 0)
            { _rows.SelectedIndex = Math.Clamp(_rows.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _rows.Items.Count - 1); e.Handled = true; }
        };
        _rows.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && !ImeComposition.IsComposing(_rows)) { e.Handled = true; Copy((Keyboard.Modifiers & ModifierKeys.Control) == 0); } };
        _master.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Unlock_Click(this, e); } };
        _master.PasswordChanged += (_, _) => { Error.Text = ""; _master.SetResourceReference(BorderBrushProperty, "RuleBrush"); _master.BorderThickness = new Thickness(1); };
        Loaded += (_, _) => { Refresh(); if (Model.IsUnlocked) _query.Focus(); else _master.Focus(); };
        ClearOnClose(() => { _all = []; _rows.ItemsSource = null; _count.Text = ""; });
    }
    private void Refresh()
    {
        _locked.Visibility = Model.Vault.IsUnlocked ? Visibility.Collapsed : Visibility.Visible;
        _search.Visibility = Model.Vault.IsUnlocked ? Visibility.Visible : Visibility.Collapsed;
        Height = Model.Vault.IsUnlocked ? 580 : 380;
        _keys.Text = Model.Vault.IsUnlocked ? "↑ ↓ 选择    Enter 复制密码    Ctrl+Enter 复制用户名    Esc 关闭" : "Enter 解锁    Esc 关闭";
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
        _count.Text = $"{_rows.Items.Count} 个账号";
        _empty.Visibility = _rows.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateActions();
    }
    private void UpdateActions()
    {
        _copyPassword.IsEnabled = _rows.SelectedItem is VaultEntrySummary;
        _copyUser.IsEnabled = _rows.SelectedItem is VaultEntrySummary;
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
        var password = _master.Password; _master.Clear(); _unlock.IsEnabled = false; _unlock.Content = "正在解锁…";
        var generation = Model.Vault.SessionVersion;
        if (await Model.RunAsync(() => Model.Vault.Unlock(password, generation)) && !Revoked)
        { Refresh(); _query.Focus(); } else Error.Text = Model.Status;
        password = "";
        _unlock.IsEnabled = true; _unlock.Content = "解锁并检索";
    }
    private void Copy(bool password)
    {
        if (Revoked || !Model.Vault.IsUnlocked || _rows.SelectedItem is not VaultEntrySummary row) return;
        if (Model.TryCopyEntry(row, password)) Close(); else Error.Text = Model.Status;
    }
}
