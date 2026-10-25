using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MemoDesk.App.ViewModels;
using MemoDesk.Platform.Windows.Hotkeys;
using MemoDesk.App.Controls;
using System.Windows.Media;

namespace MemoDesk.App.Views;

public partial class SettingsView : UserControl
{
    private bool _recording;
    private string? _pendingGesture;

    public SettingsView()
    {
        InitializeComponent();
        Unloaded += (_, _) => CancelRecordingIfNeeded();
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel && sender is PasswordBox box)
        {
            viewModel.ApiKeyInput = box.Password;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            foreach (var field in TimeFields(this).Where(f => f.IsVisible))
            {
                field.CommitText();
                if (!field.IsValid) { viewModel.StatusMessage = field.ValidationError!; return; }
            }
            viewModel.SaveCommand.Execute(null);
            ApiKeyBox.Clear();
        }
    }

    private static IEnumerable<TimeField> TimeFields(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TimeField field) yield return field;
            foreach (var nested in TimeFields(child)) yield return nested;
        }
    }

    private void RebindHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        _recording = true;
        _pendingGesture = null;
        viewModel.BeginHotkeyRecordingCommand.Execute(null);
        HotkeyBox.Focus();
        Keyboard.Focus(HotkeyBox);
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording || DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            e.Handled = true;
            CancelRecordingIfNeeded();
            return;
        }

        if (key == Key.Tab)
        {
            // Tab/Shift+Tab 离开录制控件并取消，不当作新组合。
            CancelRecordingIfNeeded();
            return;
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (HotkeyGesture.IsModifierVirtualKey(virtualKey))
        {
            return;
        }

        var modifiers = (uint)Keyboard.Modifiers;
        if (modifiers == 0)
        {
            viewModel.StatusMessage = "快捷键至少需要一个修饰键（Ctrl/Alt/Shift/Win）。";
            e.Handled = true;
            return;
        }

        _pendingGesture = HotkeyGesture.Format(modifiers, virtualKey);
        viewModel.ShowHotkeyCandidate(_pendingGesture);
        e.Handled = true;
    }

    private void HotkeyBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_recording || _pendingGesture is null)
        {
            return;
        }

        // 等按键状态更新后再判断：所有键都松开才提交，避免录制组合立即触发本应用。
        Dispatcher.BeginInvoke(
            () =>
            {
                if (_recording && _pendingGesture is not null && Keyboard.Modifiers == ModifierKeys.None)
                {
                    CommitRecording();
                }
            },
            DispatcherPriority.Input);
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_recording)
        {
            CancelRecordingIfNeeded();
        }
    }

    private void CommitRecording()
    {
        if (!_recording || _pendingGesture is null || DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        var gesture = _pendingGesture;
        _pendingGesture = null;
        _recording = false;
        viewModel.CommitHotkeyRecording(gesture);
    }

    private void CancelRecordingIfNeeded()
    {
        if (!_recording)
        {
            return;
        }

        _recording = false;
        _pendingGesture = null;
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.CancelHotkeyRecordingCommand.Execute(null);
        }
    }
}
