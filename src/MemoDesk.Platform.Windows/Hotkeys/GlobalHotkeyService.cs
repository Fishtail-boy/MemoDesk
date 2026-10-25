using System.Runtime.InteropServices;
using System.Windows.Interop;
using MemoDesk.Platform.Windows.Interop;

namespace MemoDesk.Platform.Windows.Hotkeys;

/// <summary>
/// 全局快捷键（设计 2.1）：RegisterHotKey + 消息窗口，MOD_NOREPEAT 抑制按住重复唤起。
/// 必须在拥有 UI 线程的 STA 线程上创建和注册。
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly Dictionary<int, Action> _callbacks = new();
    private HwndSource? _messageWindow;
    private int _nextId = 1;
    private bool _disposed;

    /// <summary>注册快捷键；失败抛出 <see cref="HotkeyRegistrationException"/>。</summary>
    public int Register(string gesture, Action callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gesture);
        ArgumentNullException.ThrowIfNull(callback);
        ObjectDisposedException.ThrowIf(_disposed, this);

        HotkeyGesture parsed;
        try
        {
            parsed = HotkeyGesture.Parse(gesture);
        }
        catch (FormatException ex)
        {
            throw new HotkeyRegistrationException(ex.Message);
        }

        EnsureMessageWindow();

        var id = _nextId++;
        var registered = NativeMethods.RegisterHotKey(
            _messageWindow!.Handle,
            id,
            parsed.Modifiers | NativeMethods.ModNoRepeat,
            parsed.VirtualKey);

        if (!registered)
        {
            var error = Marshal.GetLastWin32Error();
            throw new HotkeyRegistrationException(error == ErrorHotkeyAlreadyRegistered
                ? $"快捷键 {gesture} 已被其他程序占用，请关闭占用它的程序或改用其他组合。"
                : $"快捷键 {gesture} 注册失败（Win32 错误码 {error}）。");
        }

        _callbacks[id] = callback;
        return id;
    }

    public void Unregister(int id)
    {
        if (_messageWindow is not null)
        {
            _ = NativeMethods.UnregisterHotKey(_messageWindow.Handle, id);
        }

        _callbacks.Remove(id);
    }

    private void EnsureMessageWindow()
    {
        if (_messageWindow is not null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("MemoDesk.HotkeyMessageWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = NativeMethods.HwndMessage,
        };

        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotkey && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            handled = true;
            callback();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_messageWindow is not null)
        {
            foreach (var id in _callbacks.Keys.ToList())
            {
                _ = NativeMethods.UnregisterHotKey(_messageWindow.Handle, id);
            }

            _callbacks.Clear();
            _messageWindow.RemoveHook(WndProc);
            _messageWindow.Dispose();
            _messageWindow = null;
        }
    }
}
