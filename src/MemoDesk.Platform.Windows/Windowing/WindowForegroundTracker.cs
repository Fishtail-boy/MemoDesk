using MemoDesk.Platform.Windows.Interop;

namespace MemoDesk.Platform.Windows.Windowing;

/// <summary>记录唤起前的活动窗口，并在快捷输入结束后尽量恢复焦点（设计 2.1）。</summary>
public sealed class WindowForegroundTracker
{
    public IntPtr Capture() => NativeMethods.GetForegroundWindow();

    public void TryRestore(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        // 前台窗口切换受系统策略限制，失败时静默放弃，不影响捕获结果。
        _ = NativeMethods.SetForegroundWindow(windowHandle);
    }
}
