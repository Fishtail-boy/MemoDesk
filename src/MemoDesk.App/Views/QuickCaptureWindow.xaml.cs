using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MemoDesk.App.Services;
using MemoDesk.Core;
using MemoDesk.Core.Capturing;
using MemoDesk.Infrastructure.Settings;
using MemoDesk.Platform.Windows.Input;
using MemoDesk.Platform.Windows.Windowing;

namespace MemoDesk.App.Views;

/// <summary>
/// 快捷输入框（设计 2.1）：光标所在显示器弹出；原文持久化成功后才收起；
/// 失败保留输入并提示；提交后尽量恢复到唤起前的工作窗口。
/// </summary>
public partial class QuickCaptureWindow : Window
{
    private readonly MemoCaptureService _captureService;
    private readonly WindowForegroundTracker _foreground;
    private readonly MonitorLocator _monitors;
    private readonly AppSettings _settings;
    private readonly HotkeyStatus _hotkeyStatus;

    private IntPtr _previousWindow = IntPtr.Zero;
    private bool _isSubmitting;
    private Guid? _pendingCaptureId;
    private Guid? _pendingMemoId;
    private string? _pendingRawText;

    public QuickCaptureWindow(
        MemoCaptureService captureService,
        WindowForegroundTracker foreground,
        MonitorLocator monitors,
        AppSettings settings,
        HotkeyStatus hotkeyStatus)
    {
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _hotkeyStatus = hotkeyStatus ?? throw new ArgumentNullException(nameof(hotkeyStatus));

        InitializeComponent();
        WindowEffects.Attach(this, transient: true);
        HotkeyHint.Text = _hotkeyStatus.DisplayText;
        _hotkeyStatus.PropertyChanged += (_, _) =>
            Dispatcher.InvokeAsync(() => HotkeyHint.Text = _hotkeyStatus.DisplayText);

        Closing += (_, args) =>
        {
            if (Application.Current is App { IsExiting: true })
            {
                return;
            }

            // 输入框不真正关闭（复用实例），Alt+F4 等同取消本次输入。
            args.Cancel = true;
            Cancel();
        };
    }

    /// <summary>一条原文成功落盘后触发（UI 线程）。</summary>
    public event Action<Capture>? Captured;
    public event Action? VaultRequested;

    private void OpenVault_Click(object sender, RoutedEventArgs e)
    {
        if (_isSubmitting) return;
        Cancel();
        VaultRequested?.Invoke();
    }

    /// <summary>在光标所在显示器上显示并聚焦输入框；重复唤起时只复位焦点。</summary>
    public void ShowForCapture()
    {
        if (IsVisible)
        {
            Activate();
            FocusInput();
            return;
        }

        _previousWindow = _foreground.Capture();
        ResetInput();
        PlaceOnCursorMonitor();

        Show();
        Activate();
        FocusInput();
    }

    private void PlaceOnCursorMonitor()
    {
        var area = _monitors.GetWorkAreaForCursor();
        Width = Math.Min(600, Math.Max(420, area.Width - 80));
        Left = area.Left + ((area.Width - Width) / 2);
        Top = area.Top + (area.Height * 0.24);
    }

    private void FocusInput()
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            InputBox.Focus();
            Keyboard.Focus(InputBox);
            InputBox.CaretIndex = InputBox.Text.Length;
        }, DispatcherPriority.Input);
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 输入法正在使用 Esc 取消候选时，不关闭窗口。
            if (ImeComposition.IsComposing(InputBox))
            {
                return;
            }

            e.Handled = true;
            Cancel();
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            return; // Shift+Enter 换行，交给 TextBox 默认行为。
        }

        if (ImeComposition.IsComposing(InputBox))
        {
            return; // Enter 先交给输入法确认候选。
        }

        e.Handled = true;
        Submit();
    }

    private void InputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        PlaceholderText.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Submit()
    {
        var rawText = InputBox.Text;
        if (string.IsNullOrWhiteSpace(rawText))
        {
            ShowError("内容为空，没有可记录的文字。");
            return;
        }

        if (_isSubmitting)
        {
            return;
        }

        _isSubmitting = true;
        InputBox.IsReadOnly = true;
        HideError();

        if (!string.Equals(_pendingRawText, rawText, StringComparison.Ordinal))
        {
            _pendingRawText = rawText;
            _pendingCaptureId = Guid.NewGuid();
            _pendingMemoId = Guid.NewGuid();
        }

        try
        {
            var submittedAt = DateTimeOffset.UtcNow;
            // 原文、正式备忘和录入事件同一事务保存；成功后才会收起窗口。
            var result = await Task.Run(() => _captureService.Capture(
                rawText,
                _settings.TimeZoneId,
                _pendingCaptureId,
                _pendingMemoId,
                submittedAt));

            ResetInput();
            Hide();
            _foreground.TryRestore(_previousWindow);
            Captured?.Invoke(result.Capture);
        }
        catch (Exception ex)
        {
            // 磁盘写入失败等：保留输入与窗口，让用户重试。
            ShowError($"保存失败，请重试：{ex.Message}");
            InputBox.IsReadOnly = false;
            FocusInput();
        }
        finally
        {
            _isSubmitting = false;
        }
    }

    private void Cancel()
    {
        ResetInput();
        Hide();
        _foreground.TryRestore(_previousWindow);
    }

    private void ResetInput()
    {
        InputBox.IsReadOnly = false;
        InputBox.Clear();
        HideError();
        _pendingRawText = null;
        _pendingCaptureId = null;
        _pendingMemoId = null;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
    }
}
