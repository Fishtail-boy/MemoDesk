using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MemoDesk.Core.Reminders;

namespace MemoDesk.App.Controls;

/// <summary>24 小时时间输入；面板选择直到确定才提交，取消不改变原始精度。</summary>
public partial class TimeField : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string),
        typeof(TimeField), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((TimeField)d).SyncText()));
    private bool _updating;
    public TimeField()
    {
        InitializeComponent();
        Hours.ItemsSource = Enumerable.Range(0, 24).Select(n => n.ToString("00")).ToArray();
        Minutes.ItemsSource = Seconds.ItemsSource = Enumerable.Range(0, 60).Select(n => n.ToString("00")).ToArray();
        SyncText();
    }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsValid { get; private set; } = true;
    public string? ValidationError { get; private set; }
    public TimeOnly? SelectedTime { get; private set; }
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;
    public IReadOnlyList<SnoozeOption> QuickOptions { get; set; } = Array.Empty<SnoozeOption>();
    public event Action<DateTimeOffset>? TargetRequested;

    private void SyncText()
    {
        if (TextPart is null || _updating) return;
        _updating = true;
        TextPart.Text = Text ?? string.Empty;
        _updating = false;
        ClearError();
    }
    private void TextPart_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updating) SetCurrentValue(TextProperty, TextPart.Text);
    }
    public void CommitText()
    {
        var value = Text.Trim();
        if (value.Length == 0) { SelectedTime = null; ClearError(); return; }
        if (TimeOnly.TryParseExact(value, new[] { "HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss" },
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        { SelectedTime = time; ClearError(); return; }
        IsValid = false; ValidationError = "时间无效，请输入 HH:mm 或 HH:mm:ss。";
        TextPart.ToolTip = ValidationError;
        TextPart.SetResourceReference(TextBox.ForegroundProperty, "DangerBrush");
    }
    private void ClearError()
    {
        IsValid = true; ValidationError = null;
        if (TextPart is null) return;
        TextPart.ToolTip = "HH:mm 或 HH:mm:ss";
        TextPart.SetResourceReference(TextBox.ForegroundProperty, "InkBrush");
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        CommitText();
        var time = IsValid && SelectedTime is { } selected ? selected : TimeOnly.FromDateTime(DateTime.Now);
        Hours.SelectedIndex = time.Hour; Minutes.SelectedIndex = time.Minute; Seconds.SelectedIndex = time.Second;
        SecondsToggle.IsChecked = Text.Count(c => c == ':') > 1;
        SecondsPanel.Visibility = SecondsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Shortcuts.ItemsSource = QuickOptions;
        TimePopup.IsOpen = true;
        Hours.Focus();
    }
    private void Apply_Click(object sender, RoutedEventArgs e) => Apply();
    private void Apply()
    {
        if (Hours.SelectedIndex < 0 || Minutes.SelectedIndex < 0) return;
        var time = new TimeOnly(Hours.SelectedIndex, Minutes.SelectedIndex,
            SecondsToggle.IsChecked == true ? Math.Max(0, Seconds.SelectedIndex) : 0);
        SetCurrentValue(TextProperty, time.ToString(SecondsToggle.IsChecked == true ? "HH:mm:ss" : "HH:mm"));
        SyncText(); CommitText(); TimePopup.IsOpen = false;
    }
    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SnoozeOption option })
        { TargetRequested?.Invoke(SnoozeCalculator.Compute(option, DateTimeOffset.UtcNow, TimeZone)); TimePopup.IsOpen = false; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => TimePopup.IsOpen = false;
    private void Popup_Closed(object? sender, EventArgs e) => TextPart.Focus();
    private void Seconds_Changed(object sender, RoutedEventArgs e)
    { if (SecondsPanel is not null) SecondsPanel.Visibility = SecondsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }
    private void TextPart_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    { if (!TimePopup.IsOpen) CommitText(); }
    private void TextPart_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitText(); e.Handled = true; }
        else if (e.Key == Key.Down && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { Open_Click(sender, e); e.Handled = true; }
    }
    private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { TimePopup.IsOpen = false; e.Handled = true; }
        else if (e.Key == Key.Enter) { Apply(); e.Handled = true; }
    }
}
