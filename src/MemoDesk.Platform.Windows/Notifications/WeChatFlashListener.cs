using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MemoDesk.Platform.Windows.Notifications;

/// <summary>Tracks WeChat windows that ask the Windows shell for attention.</summary>
public sealed class WeChatFlashListener : IDisposable
{
    private const int Flash = 0x8006;
    private const int Activated = 4;
    private const int RudeActivated = 0x8004;
    private const int Destroyed = 2;
    private readonly HwndSource _source;
    private readonly int _message;
    private readonly Dictionary<IntPtr, uint> _unread = new();

    public WeChatFlashListener(HwndSource source)
    {
        _source = source;
        _message = RegisterWindowMessage("SHELLHOOK");
        IsRegistered = _message != 0 && RegisterShellHookWindow(source.Handle);
        if (IsRegistered) _source.AddHook(OnMessage);
    }

    public bool IsRegistered { get; }
    public int Count => _unread.Count;
    public event Action? Changed;

    public bool OpenLatest()
    {
        foreach (var (window, pid) in _unread.Reverse().ToArray())
        {
            if (!IsWeChatWindow(window, out var actualPid) || actualPid != pid)
            {
                Remove(window);
                continue;
            }

            if (IsIconic(window)) ShowWindowAsync(window, 9);
            if (!SetForegroundWindow(window) && GetForegroundWindow() != window) return false;
            Remove(window);
            return true;
        }

        return false;
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr window, ref bool handled)
    {
        if (message != _message) return IntPtr.Zero;
        switch (wParam.ToInt64())
        {
            case Flash when window != GetForegroundWindow() && !_unread.ContainsKey(window)
                && IsWeChatWindow(window, out var pid):
                _unread.Add(window, pid);
                Changed?.Invoke();
                break;
            case Activated:
            case RudeActivated:
            case Destroyed:
                Remove(window);
                break;
        }
        return IntPtr.Zero;
    }

    private void Remove(IntPtr window)
    {
        if (_unread.Remove(window)) Changed?.Invoke();
    }

    private static bool IsWeChatWindow(IntPtr window, out uint pid)
    {
        GetWindowThreadProcessId(window, out pid);
        if (pid == 0 || !IsWindowVisible(window)) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.Equals("WeChat", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("Weixin", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (!IsRegistered || _source.IsDisposed) return;
        _source.RemoveHook(OnMessage);
        DeregisterShellHookWindow(_source.Handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
