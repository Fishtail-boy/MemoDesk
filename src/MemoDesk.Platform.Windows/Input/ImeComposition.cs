using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MemoDesk.Platform.Windows.Interop;

namespace MemoDesk.Platform.Windows.Input;

/// <summary>
/// 输入法组合状态检测（设计 2.1）：中文输入法候选确认期间，Enter 应先交给输入法，
/// 避免误提交。使用 IMM32 的组合串长度判断，覆盖 WPF 键盘事件早于输入法处理的情况。
/// </summary>
public static class ImeComposition
{
    public static bool IsComposing(Visual visual)
    {
        ArgumentNullException.ThrowIfNull(visual);

        if (PresentationSource.FromVisual(visual) is not HwndSource source)
        {
            return false;
        }

        var context = NativeMethods.ImmGetContext(source.Handle);
        if (context == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return NativeMethods.ImmGetCompositionString(context, NativeMethods.GcsCompStr, IntPtr.Zero, 0) > 0;
        }
        finally
        {
            _ = NativeMethods.ImmReleaseContext(source.Handle, context);
        }
    }
}
