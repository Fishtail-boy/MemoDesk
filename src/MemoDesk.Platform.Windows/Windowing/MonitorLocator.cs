using System.Runtime.InteropServices;
using System.Windows;
using MemoDesk.Platform.Windows.Interop;

namespace MemoDesk.Platform.Windows.Windowing;

/// <summary>工作区矩形，单位为 WPF 设备无关像素（DIP）。</summary>
public readonly record struct WorkArea(double Left, double Top, double Width, double Height);

/// <summary>定位鼠标所在显示器的可用工作区，供快捷输入框在当前显示器上出现。</summary>
public sealed class MonitorLocator
{
    private const int MdtEffectiveDpi = 0;

    public IReadOnlyList<WorkArea> GetWorkAreas()
    {
        var areas = new List<WorkArea>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.Point
                { X = screen.Bounds.Left + 1, Y = screen.Bounds.Top + 1 }, NativeMethods.MonitorDefaultToNearest);
            var scale = monitor == IntPtr.Zero ? 1 : GetDipScale(monitor);
            var rect = screen.WorkingArea;
            areas.Add(new WorkArea(rect.Left * scale, rect.Top * scale, rect.Width * scale, rect.Height * scale));
        }
        return areas;
    }

    public WorkArea GetWorkAreaForCursor()
    {
        if (NativeMethods.GetCursorPos(out var cursor))
        {
            var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                var info = new NativeMethods.MonitorInfo
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>(),
                };

                if (NativeMethods.GetMonitorInfo(monitor, ref info))
                {
                    var scale = GetDipScale(monitor);
                    return new WorkArea(
                        info.Work.Left * scale,
                        info.Work.Top * scale,
                        (info.Work.Right - info.Work.Left) * scale,
                        (info.Work.Bottom - info.Work.Top) * scale);
                }
            }
        }

        var fallback = SystemParameters.WorkArea;
        return new WorkArea(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
    }

    private static double GetDipScale(IntPtr monitor)
    {
        if (NativeMethods.GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
        {
            return 96.0 / dpiX;
        }

        return 1.0;
    }
}
