using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Brushes = System.Windows.Media.Brushes;
using SystemColors = System.Windows.SystemColors;

namespace MemoDesk.Platform.Windows.Windowing;

/// <summary>公开 DWM 材质与被动窗口行为；旧系统和关闭透明时保持实色可读。</summary>
public static class WindowEffects
{
    private const int ExStyle = -20;
    private const long NoActivate = 0x08000000;
    private const long ToolWindow = 0x00000080;
    public static void Attach(Window window, bool transient = false, bool passive = false)
    {
        window.ShowActivated = !passive;
        void Apply() { ApplyBackdrop(window, transient); window.InvalidateVisual(); }
        window.SourceInitialized += (_, _) =>
        {
            if (passive) SetPassive(window, true);
            if (passive)
                HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(
                    (IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled) =>
                    {
                        if (message == 0x0021 && IsPassive(hwnd)) { handled = true; return new IntPtr(3); }
                        return IntPtr.Zero;
                    });
        };
        // 等 WPF 首帧绘制完再扩展 DWM 材质，避免部分显卡首帧白屏、调整大小后才显示内容。
        EventHandler? rendered = null;
        rendered = (_, _) =>
        {
            window.ContentRendered -= rendered;
            window.Dispatcher.InvokeAsync(Apply, System.Windows.Threading.DispatcherPriority.Background);
        };
        window.ContentRendered += rendered;
        System.ComponentModel.PropertyChangedEventHandler changed = (_, _) =>
        { if (!window.Dispatcher.HasShutdownStarted) window.Dispatcher.InvokeAsync(Apply); };
        SystemParameters.StaticPropertyChanged += changed;
        window.Closed += (_, _) => SystemParameters.StaticPropertyChanged -= changed;
        if (passive) window.Deactivated += (_, _) => SetPassive(window, true);
    }
    public static void Focus(Window window)
    {
        SetPassive(window, false);
        window.Activate(); window.Focus();
    }
    public static void SetPassive(Window window, bool value)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var flags = GetStyle(hwnd).ToInt64();
        SetStyle(hwnd, new IntPtr(value ? flags | NoActivate | (window.ShowInTaskbar ? 0 : ToolWindow) : flags & ~NoActivate));
    }
    private static bool IsPassive(IntPtr hwnd) => (GetStyle(hwnd).ToInt64() & NoActivate) != 0;
    private static IntPtr GetStyle(IntPtr hwnd) => IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, ExStyle) : new(GetWindowLong(hwnd, ExStyle));
    private static void SetStyle(IntPtr hwnd, IntPtr value)
    { if (IntPtr.Size == 8) SetWindowLongPtr(hwnd, ExStyle, value); else SetWindowLong(hwnd, ExStyle, value.ToInt32()); }
    private static void ApplyBackdrop(Window window, bool transient)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var fallback = window.TryFindResource("PaperBrush") as SolidColorBrush;
        var color = SystemParameters.HighContrast ? SystemColors.WindowColor : fallback?.Color ?? Colors.White;
        color.A = 255;
        window.Background = new SolidColorBrush(color);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || window.AllowsTransparency) return;
        var enabled = !SystemParameters.HighContrast && TransparencyEnabled() && !BatterySaver();
        var backdrop = enabled ? (transient ? 3 : 2) : 1;
        if (DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int)) == 0 && enabled)
        {
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            window.Background = Brushes.Transparent;
        }
        var corners = 2;
        DwmSetWindowAttribute(hwnd, 33, ref corners, sizeof(int));
        var dark = 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }
    private static bool TransparencyEnabled()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return key?.GetValue("EnableTransparency") is not int value || value != 0; }
        catch (System.Security.SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.IO.IOException) { return false; }
    }
    private static bool BatterySaver() => GetSystemPowerStatus(out var status) && status.Saver == 1;
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte Ac, Flag, Percent, Saver; public uint Life, FullLife; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd,int index,IntPtr value);
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd,int index);
    [DllImport("user32.dll", EntryPoint="SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd,int index,int value);
}
