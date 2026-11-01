using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MemoDesk.App.ViewModels;
using MemoDesk.Platform.Windows.Hotkeys;
using MemoDesk.Platform.Windows.Windowing;
using MemoDesk.Vault.Core;

namespace MemoDesk.App.Views;

/// <summary>密码窗口统一跟随会话撤销，关闭时清空输入控件。</summary>
public class VaultDialogWindow : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(24) };
    protected readonly StackPanel Footer = new() { Margin = new Thickness(24, 0, 24, 18), Visibility = Visibility.Collapsed };
    protected readonly TextBlock Error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    protected readonly VaultViewModel Model;
    private readonly List<Action> _clear = [];
    private readonly Action _locked;
    private readonly long _generation;
    private bool _revoked;
    protected bool Revoked => _revoked || Model.Vault.SessionVersion != _generation;
    protected VaultDialogWindow(VaultViewModel model, string title, double width = 480, double height = 600)
    {
        Model = model; Title = title; Width = width; Height = height; MinWidth = width;
        MaxHeight = SystemParameters.WorkArea.Height - 50;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        SetResourceReference(BackgroundProperty, "PaperBrush");
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetRow(Footer, 1); layout.Children.Add(Footer); Content = layout;
        _generation = model.Vault.SessionVersion;
        _locked = () =>
        {
            _revoked = true;
            if (Dispatcher.CheckAccess()) Revoke(); else Dispatcher.BeginInvoke(Revoke);
        };
        model.Vault.Locked += _locked;
        Closed += (_, _) => { model.Vault.Locked -= _locked; Scrub(); };
        Loaded += (_, _) => { if (Revoked) Revoke(); };
        PreviewMouseDown += (_, _) => model.Touch();
        PreviewKeyDown += (_, e) => { model.Touch(); if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        WindowEffects.Attach(this, transient: true);
        Heading(title);
        Error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
    }
    private void Revoke() { Scrub(); if (IsVisible) Close(); }
    protected void ClearOnClose(Action action) => _clear.Add(action);
    private void Scrub() { foreach (var action in _clear) action(); Error.Text = ""; }
    protected void Heading(string text)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        label.SetResourceReference(StyleProperty, "SectionTitle"); Body.Children.Add(label);
    }
    protected void Note(string text)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) };
        label.SetResourceReference(StyleProperty, "MetaText"); Body.Children.Add(label);
    }
    protected TextBox Field(string label, string value = "", bool multiline = false)
    {
        Note(label);
        var box = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 10), AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MaxLength = multiline ? 65536 : 2048 };
        box.SetResourceReference(StyleProperty, "FieldBox");
        if (multiline) { box.Height = 76; box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
        Body.Children.Add(box); ClearOnClose(box.Clear); return box;
    }
    protected PasswordBox PasswordField(string label, string value = "", int maxLength = 65536)
    {
        Note(label);
        var box = new PasswordBox { Password = value, Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 0, 0, 12), MaxLength = maxLength };
        box.SetResourceReference(BorderBrushProperty, "RuleBrush"); Body.Children.Add(box); ClearOnClose(box.Clear); return box;
    }
    protected Button ActionButton(string text, RoutedEventHandler handler, bool primary = false)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        button.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : "GhostButton");
        button.Click += handler; Body.Children.Add(button); return button;
    }
    protected Button FooterButton(string text, RoutedEventHandler handler)
    {
        var button = ActionButton(text, handler, primary: true); Body.Children.Remove(button);
        Footer.Children.Add(button); Footer.Visibility = Visibility.Visible; return button;
    }
}

public sealed class VaultEditWindow : VaultDialogWindow
{
    private readonly TextBox _title, _user, _url, _category, _tags, _notes;
    private readonly PasswordBox _password;
    private readonly CheckBox _favorite = new() { Content = "收藏这个账号", Margin = new Thickness(0, 8, 0, 8) };
    private readonly VaultEntrySummary? _entry;
    public VaultEditWindow(VaultViewModel model, VaultEntrySummary? entry = null) : base(model, entry is null ? "新建账号" : "编辑账号", 500, 760)
    {
        _entry = entry;
        using var secret = entry is null ? null : model.Vault.ReadSecret(entry.Id, entry.RowVersion);
        _title = Field("名称 *", entry?.Title ?? ""); _user = Field("用户名", entry?.UserName ?? "");
        _password = PasswordField("密码", secret?.Password ?? "");
        ActionButton("生成 24 位随机密码", (_, _) => _password.Password = PasswordGenerator.Generate());
        _url = Field("网址", entry?.Url ?? ""); _category = Field("分类（可直接输入新分类）", entry?.Category ?? "个人");
        _tags = Field("标签", entry?.Tags ?? ""); _notes = Field("保密备注", secret?.Notes ?? "", multiline: true);
        _favorite.IsChecked = entry?.Favorite ?? false; Body.Children.Add(_favorite);
        Note("点击保存后生效；自动锁定会丢弃未保存的修改。");
        FooterButton("保存账号", Save_Click); Footer.Children.Add(Error);
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Revoked || Model.IsBusy) return;
        using var draft = new VaultEntryDraft
        {
            Id = _entry?.Id ?? Guid.NewGuid(), ExpectedVersion = _entry?.RowVersion ?? 0,
            Title = _title.Text, UserName = _user.Text, Password = _password.Password, Url = _url.Text,
            Category = _category.Text, Tags = _tags.Text, Notes = _notes.Text, Favorite = _favorite.IsChecked == true
        };
        Body.IsEnabled = false;
        try
        {
            var saved = await Model.RunAsync(() => Model.Vault.Save(draft), "账号已保存。");
            if (saved && !Revoked && IsVisible) Close(); else if (!Revoked) Error.Text = Model.Status;
        }
        finally { Body.IsEnabled = true; }
    }
}

public sealed class VaultPasswordWindow : VaultDialogWindow, IDisposable
{
    private readonly PasswordBox _password;
    private readonly PasswordBox? _current, _confirm;
    public string Password { get; private set; } = "";
    public string CurrentPassword { get; private set; } = "";
    public VaultPasswordWindow(string title, VaultViewModel model, bool change = false) : base(model, title, 470, change ? 600 : 340)
    {
        var validationShown = false;
        if (change) _current = PasswordField("当前主密码", maxLength: 0);
        _password = PasswordField(change ? "新主密码" : "备份主密码", maxLength: 0);
        if (change)
        {
            Note(MasterPasswordPolicy.Requirements);
            _confirm = PasswordField("再次输入新主密码", maxLength: 0);
            Note("旧备份仍需要旧主密码。请保管好新旧备份对应的口令。");
        }
        var errorPanel = new Border { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 8, 0, 0), Child = Error };
        Error.Margin = new Thickness(0); Error.FontWeight = FontWeights.SemiBold;
        errorPanel.SetResourceReference(Border.BackgroundProperty, "WarnSoftBrush"); errorPanel.SetResourceReference(Border.BorderBrushProperty, "DangerBrush");
        FooterButton("确认", (_, _) =>
        {
            if (Revoked) return;
            if (!Validate(focus: true)) return;
            Password = _password.Password; CurrentPassword = _current?.Password ?? ""; DialogResult = true;
        });
        Footer.Children.Insert(0, errorPanel);
        foreach (var field in new[] { _current, _password, _confirm }.OfType<PasswordBox>())
            field.PasswordChanged += (_, _) => { if (validationShown && !Revoked) Validate(focus: false); };
        bool Validate(bool focus)
        {
            var error = _current is null ? null : MasterPasswordPolicy.GetError(_current.Password, creating: false);
            var invalid = _current ?? _password;
            if (error is null)
            {
                invalid = _password;
                error = MasterPasswordPolicy.GetError(_password.Password, creating: change);
            }
            if (error is null && _confirm is not null && _password.Password != _confirm.Password)
            {
                invalid = _confirm;
                error = _confirm.Password.Length == 0 ? "请再次输入新主密码以确认。" : "两次输入的新主密码不一致，请重新确认。";
            }
            foreach (var field in new[] { _current, _password, _confirm }.OfType<PasswordBox>())
            { field.SetResourceReference(BorderBrushProperty, "RuleBrush"); field.BorderThickness = new Thickness(1); }
            validationShown = error is not null; Error.Text = error ?? "";
            errorPanel.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
            if (error is null) return true;
            invalid.SetResourceReference(BorderBrushProperty, "DangerBrush"); invalid.BorderThickness = new Thickness(2);
            if (focus) { invalid.Focus(); invalid.BringIntoView(); }
            return false;
        }
    }
    public void Dispose() { Password = CurrentPassword = ""; _password.Clear(); _current?.Clear(); _confirm?.Clear(); GC.SuppressFinalize(this); }
}

public sealed class VaultGeneratorWindow : VaultDialogWindow
{
    private readonly ComboBox _length = new() { ItemsSource = new[] { 16, 20, 24, 32, 48, 64 }, SelectedItem = 24, Margin = new Thickness(0, 8, 0, 12) };
    private readonly CheckBox[] _sets;
    private readonly TextBox _result;
    public VaultGeneratorWindow(VaultViewModel model) : base(model, "密码生成器", 480, 520)
    {
        Note("长度"); Body.Children.Add(_length);
        _sets = new[] { "大写字母", "小写字母", "数字", "符号" }.Select(s => new CheckBox { Content = s, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) }).ToArray();
        foreach (var set in _sets) Body.Children.Add(set);
        _result = Field("生成结果"); _result.IsReadOnly = true; _result.FontFamily = new FontFamily("Consolas");
        _result.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            (_, e) => { CopyGeneratedPassword(_result.SelectedText); e.Handled = true; },
            (_, e) =>
            {
                e.CanExecute = !Revoked && Model.Vault.IsUnlocked && _result.SelectionLength > 0;
                e.Handled = true;
            }));
        ActionButton("重新生成", (_, _) => Generate());
        ActionButton("复制密码", (_, _) => CopyGeneratedPassword(_result.Text), primary: true);
        Body.Children.Add(Error); Deactivated += (_, _) => _result.Clear(); Generate();
    }
    private void CopyGeneratedPassword(string value)
    {
        if (Revoked || !Model.Vault.IsUnlocked || value.Length == 0) return;
        Model.Touch();
        var copied = Model.Clipboard.Copy(value);
        if (Revoked || !Model.Vault.IsUnlocked) { Model.Clipboard.ClearOwned(); return; }
        Error.Text = copied ? "已复制，将在 20 秒后清除。" : "复制失败，请重试。";
    }
    private void Generate()
    {
        if (Revoked || !Model.IsUnlocked) return;
        try { _result.Text = PasswordGenerator.Generate((int)_length.SelectedItem, _sets[0].IsChecked == true, _sets[1].IsChecked == true, _sets[2].IsChecked == true, _sets[3].IsChecked == true); Error.Text = ""; }
        catch (VaultException ex) { Error.Text = VaultViewModel.ErrorText(ex.Error); }
    }
}

public sealed class VaultImportWindow : VaultDialogWindow
{
    private sealed record Column(int Index, string Label);
    private readonly VaultCsv _csv;
    private readonly ComboBox[] _maps;
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) };
    private readonly CheckBox _skip = new() { Content = "跳过同名且同用户名的条目", Margin = new Thickness(0, 12, 0, 8) };
    private readonly Button _import;
    private IReadOnlyList<VaultEntryDraft> _prepared = [];
    private readonly Guid _batch = Guid.NewGuid();
    public VaultImportWindow(VaultViewModel model, VaultCsv csv) : base(model, "导入 CSV", 500, 730)
    {
        _csv = csv;
        Note($"读取到 {csv.Count} 行。选择字段映射并预览后导入；导入完成后请自行处理来源明文文件。");
        var labels = new[] { "名称 *", "用户名", "密码 *", "网址", "保密备注", "分类" };
        var detected = new[]
        {
            csv.FindColumn("name", "title", "名称"), csv.FindColumn("username", "user", "login_username", "用户名"),
            csv.FindColumn("password", "login_password", "密码"), csv.FindColumn("url", "login_uri", "website", "网址"),
            csv.FindColumn("notes", "note", "备注"), csv.FindColumn("category", "folder", "分类")
        };
        var columns = new[] { new Column(-1, "不导入此字段") }.Concat(csv.Headers.Select((h, i) => new Column(i, $"{i + 1}. {h[..Math.Min(h.Length, 48)]}"))).ToArray();
        _maps = new ComboBox[6];
        for (var i = 0; i < 6; i++)
        {
            Note(labels[i]); var combo = new ComboBox { ItemsSource = columns, DisplayMemberPath = "Label", SelectedValuePath = "Index", SelectedValue = detected[i], Margin = new Thickness(0, 0, 0, 10) };
            Body.Children.Add(combo); _maps[i] = combo;
        }
        Body.Children.Add(_skip); ActionButton("预览导入", Preview_Click); Body.Children.Add(_preview);
        _import = FooterButton("确认导入", Import_Click); _import.IsEnabled = false; Footer.Children.Add(Error);
        foreach (var map in _maps) map.SelectionChanged += (_, _) => Invalidate();
        _skip.Checked += (_, _) => Invalidate(); _skip.Unchecked += (_, _) => Invalidate();
        ClearOnClose(() => { Invalidate(); _preview.Text = ""; });
    }
    private void Invalidate()
    { foreach (var entry in _prepared) entry.Dispose(); _prepared = []; _import.IsEnabled = false; _preview.Text = ""; }
    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (Revoked || !Model.IsUnlocked) return;
        Invalidate();
        try
        {
            var positions = _maps.Select(c => c.SelectedValue is int i ? i : -1).ToArray();
            var rows = _csv.Map(positions[0], positions[1], positions[2], positions[3], positions[4], positions[5]);
            _prepared = rows;
            var seen = Model.Vault.List().Select(r => r.Title + "\0" + r.UserName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var accepted = new List<VaultEntryDraft>(); var duplicates = 0;
            foreach (var row in rows)
            {
                var duplicate = !seen.Add(row.Title + "\0" + row.UserName);
                if (duplicate) duplicates++;
                if (duplicate && _skip.IsChecked == true) row.Dispose(); else accepted.Add(row);
            }
            _prepared = accepted;
            _preview.Text = $"将导入 {accepted.Count} 条；发现 {duplicates} 条同名同用户名记录。\n" + string.Join("、", accepted.Take(4).Select(r => r.Title[..Math.Min(60, r.Title.Length)]));
            _import.IsEnabled = accepted.Count != 0; Error.Text = "";
        }
        catch (VaultException ex) { Invalidate(); Error.Text = VaultViewModel.ErrorText(ex.Error); }
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (Revoked || Model.IsBusy || _prepared.Count == 0) return;
        var entries = _prepared; _prepared = []; Body.IsEnabled = false;
        try
        {
            if (await Model.RunAsync(() => Model.Vault.Import(entries, _batch), $"已导入 {entries.Count} 条账号。") && !Revoked && IsVisible) Close();
            else if (!Revoked) { Error.Text = Model.Status; _import.IsEnabled = false; }
        }
        finally { foreach (var entry in entries) entry.Dispose(); Body.IsEnabled = true; }
    }
}

public sealed class VaultSettingsWindow : VaultDialogWindow
{
    private readonly TextBlock _gesture = new() { Margin = new Thickness(0, 8, 0, 12) };
    private readonly ComboBox _minutes;
    private bool _recording;
    private string? _candidate;
    public VaultSettingsWindow(VaultViewModel model) : base(model, "密码库安全设置", 470, 480)
    {
        Note("无操作自动锁定（分钟）");
        _minutes = new ComboBox { ItemsSource = new[] { 1, 5, 15 }, SelectedItem = model.Settings.VaultIdleMinutes, Margin = new Thickness(0, 8, 0, 16) };
        Body.Children.Add(_minutes); Note("快捷检索组合键"); _gesture.Text = model.Hotkeys.Gesture; Body.Children.Add(_gesture);
        ActionButton("重新绑定", (_, _) => { _recording = true; Model.Hotkeys.Recording = true; _gesture.Text = "请按组合键，松开后生效；Esc 取消。"; });
        Note("窗口隐藏、最小化、系统锁屏和休眠时立即锁定。复制内容 20 秒后清除。");
        ActionButton("保存设置", (_, _) =>
        {
            try { model.Settings.VaultIdleMinutes = (int)_minutes.SelectedItem; Close(); }
            catch (Exception) { Error.Text = "设置保存失败，请重试。"; }
        }, primary: true);
        Body.Children.Add(Error); Error.Text = model.Hotkeys.Status;
        PreviewKeyDown += Record_KeyDown;
        PreviewKeyUp += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!_recording || _candidate is null || Keyboard.Modifiers != ModifierKeys.None) return;
            var gesture = _candidate; _candidate = null; _recording = false; Model.Hotkeys.Recording = false;
            Error.Text = Model.Hotkeys.Apply(gesture) ?? "快捷键已更新。"; _gesture.Text = Model.Hotkeys.Gesture;
        }, DispatcherPriority.Input);
        Closed += (_, _) => Model.Hotkeys.Recording = false;
    }
    private void Record_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (HotkeyGesture.IsModifierVirtualKey(vk)) return;
        e.Handled = true;
        if (Keyboard.Modifiers == ModifierKeys.None) { Error.Text = "请至少包含 Ctrl、Alt、Shift 或 Win 中的一个修饰键。"; return; }
        _candidate = HotkeyGesture.Format((uint)Keyboard.Modifiers, vk); _gesture.Text = _candidate;
    }
}
