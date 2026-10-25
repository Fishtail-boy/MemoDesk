using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MemoDesk.App.Controls;

/// <summary>
/// 统一日期字段：固定 yyyy-MM-dd 显示，支持键入、日历弹层、今天/明天/清除。
/// 非法输入保留文本并提示，不会静默变成今天。
/// </summary>
public partial class DateField : UserControl
{
    public static readonly DependencyProperty SelectedDateProperty = DependencyProperty.Register(
        nameof(SelectedDate),
        typeof(DateTime?),
        typeof(DateField),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedDateChanged));

    private static readonly string[] Formats = { "yyyy-MM-dd", "yyyy/MM/dd" };
    private bool _updatingText;
    private bool _openingPopup;

    public DateField()
    {
        InitializeComponent();
        UpdateText();
    }

    public bool IsValid { get; private set; } = true;

    public string? ValidationError { get; private set; }

    public DateTime? SelectedDate
    {
        get => (DateTime?)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    /// <summary>提交当前文本并更新验证状态；供保存/查询前调用。</summary>
    public void CommitText() => TryCommitText();

    private static void OnSelectedDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DateField field)
        {
            field.UpdateText();
        }
    }

    private void UpdateText()
    {
        _updatingText = true;
        try
        {
            TextPart.Text = SelectedDate is { } date
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : string.Empty;
            ClearError();
        }
        finally
        {
            _updatingText = false;
        }
    }

    private void TextPart_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryCommitText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            UpdateText();
            DatePopup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Down && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            OpenPopup();
            e.Handled = true;
        }
    }

    private void TextPart_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!DatePopup.IsOpen)
        {
            TryCommitText();
        }
    }

    private void DropButton_Click(object sender, RoutedEventArgs e) => OpenPopup();

    private void OpenPopup()
    {
        if (!IsEnabled)
        {
            return;
        }

        _openingPopup = true;
        try
        {
            Calendar.SelectedDate = SelectedDate;
            Calendar.DisplayDate = SelectedDate ?? DateTime.Today;
            DatePopup.IsOpen = true;
        }
        finally
        {
            _openingPopup = false;
        }

        Calendar.Focus();
    }

    private void Calendar_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_openingPopup || Calendar.SelectedDate is not { } date)
        {
            return;
        }

        SetCurrentValue(SelectedDateProperty, date);
        UpdateText();
        DatePopup.IsOpen = false;
        TextPart.Focus();
    }

    private void Calendar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DatePopup.IsOpen = false;
            TextPart.Focus();
            e.Handled = true;
        }
    }

    private void Calendar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not CalendarDayButton)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        if (source is not CalendarDayButton)
        {
            return;
        }

        // SelectedDatesChanged 不会在重复选择同一日期时触发；在日历处理完
        // 鼠标事件后，用当前选择值显式同步输入框并关闭弹层。
        Dispatcher.BeginInvoke(
            () =>
            {
                if (DatePopup.IsOpen && Calendar.SelectedDate is { } date)
                {
                    SetCurrentValue(SelectedDateProperty, date);
                    UpdateText();
                    DatePopup.IsOpen = false;
                    TextPart.Focus();
                }
            },
            DispatcherPriority.Input);
    }

    private void Today_Click(object sender, RoutedEventArgs e) => SetFromButton(DateTime.Today);

    private void Tomorrow_Click(object sender, RoutedEventArgs e) => SetFromButton(DateTime.Today.AddDays(1));

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SetCurrentValue(SelectedDateProperty, null);
        UpdateText();
        DatePopup.IsOpen = false;
        TextPart.Focus();
    }

    private void SetFromButton(DateTime date)
    {
        SetCurrentValue(SelectedDateProperty, date);
        UpdateText();
        DatePopup.IsOpen = false;
        TextPart.Focus();
    }

    private void TryCommitText()
    {
        if (_updatingText)
        {
            return;
        }

        var text = TextPart.Text.Trim();
        if (text.Length == 0)
        {
            SetCurrentValue(SelectedDateProperty, null);
            ClearError();
            return;
        }

        if (DateTime.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            SetCurrentValue(SelectedDateProperty, date);
            ClearError();
            return;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            SetCurrentValue(SelectedDateProperty, date);
            ClearError();
            return;
        }

        ShowError("日期格式无效，请使用 yyyy-MM-dd。");
    }

    private void ShowError(string message)
    {
        IsValid = false;
        ValidationError = message;
        TextPart.ToolTip = message;
        TextPart.SetResourceReference(TextBox.ForegroundProperty, "DangerBrush");
    }

    private void ClearError()
    {
        IsValid = true;
        ValidationError = null;
        TextPart.ToolTip = null;
        TextPart.SetResourceReference(TextBox.ForegroundProperty, "InkBrush");
    }
}
