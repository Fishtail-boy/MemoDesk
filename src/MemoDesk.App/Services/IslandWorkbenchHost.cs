using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MemoDesk.App.ViewModels;

namespace MemoDesk.App.Services;

/// <summary>Hosts the existing workbench pages in the native island settings window.</summary>
internal sealed class IslandWorkbenchHost : IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly VaultLockCoordinator _vault;
    private Window? _window;
    private IntPtr _parent;
    private string? _page;
    private readonly DispatcherTimer _parentTimer;
    private bool _parentMinimized;
    private bool? _dark;

    public IslandWorkbenchHost(MainViewModel viewModel, VaultLockCoordinator vault)
    {
        _viewModel = viewModel;
        _vault = vault;
        _parentTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _parentTimer.Tick += (_, _) =>
        {
            if (_parent == IntPtr.Zero || !IsWindow(_parent))
            {
                Close();
                return;
            }
            var minimized = IsIconic(_parent);
            if (minimized && !_parentMinimized && _page == "vault") _vault.Lock();
            _parentMinimized = minimized;
        };
    }

    public void Show(int processId, string page, int x, int y, int width, int height, bool dark)
    {
        if (width < 1 || height < 1 || page is not ("memos" or "vault" or "assets" or "worklogs" or "reminders" or "settings"))
            return;
        var parent = FindSettingsWindow(processId);
        if (parent == IntPtr.Zero) return;
        if (_window is null || _parent != parent)
        {
            Close();
            _parent = parent;
            _window = new Window
            {
                Title = "MemoDesk",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = new ContentControl { MinWidth = 480 },
                },
            };
            _window.SetResourceReference(Window.BackgroundProperty, "PaperBrush");
            _window.SetResourceReference(Window.ForegroundProperty, "InkBrush");
            _window.Closed += (_, _) =>
            {
                var wasVault = _page == "vault";
                _parentTimer.Stop();
                _window = null;
                _parent = IntPtr.Zero;
                _page = null;
                _dark = null;
                if (wasVault) _vault.Lock();
            };
            var child = new WindowInteropHelper(_window).EnsureHandle();
            var style = GetWindowLongPtr(child, -16).ToInt64();
            SetWindowLongPtr(child, -16, new IntPtr((style & ~0x80000000L) | 0x40000000L));
            SetParent(child, parent);
            _window.Show();
            _parentMinimized = false;
            _parentTimer.Start();
        }

        if (_dark != dark)
        {
            ApplyTheme(_window, dark);
            _dark = dark;
        }
        if (_page != page)
        {
            if (_page == "vault") _vault.Lock();
            var scroll = (ScrollViewer)_window.Content;
            var content = (ContentControl)scroll.Content;
            content.MinWidth = page == "vault" ? 680 : 480;
            content.Content = _viewModel.LoadPage(page).ViewModel;
            scroll.ScrollToLeftEnd();
            _page = page;
        }
        var viewport = (ScrollViewer)_window.Content;
        var minWidth = ((ContentControl)viewport.Content).MinWidth;
        viewport.HorizontalScrollBarVisibility = width / VisualTreeHelper.GetDpi(_window).DpiScaleX < minWidth
            ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        SetWindowPos(new WindowInteropHelper(_window).Handle, IntPtr.Zero, x, y, width, height, 0x0014 | 0x0040);
    }

    public void RefreshCurrent()
    {
        if (_page is { } page && page != _viewModel.CurrentKey) _viewModel.RefreshPage(page);
    }

    private static void ApplyTheme(Window window, bool dark)
    {
        var colors = dark
            ? new Dictionary<string, string>
            {
                ["PaperBrush"] = "#19191B", ["SheetBrush"] = "#212124", ["CoverBrush"] = "#212124",
                ["InkBrush"] = "#F5F5F5", ["BodyBrush"] = "#E1E1E3", ["MutedBrush"] = "#B4B4BA",
                ["FaintBrush"] = "#A0A0A7", ["CoverTextBrush"] = "#F5F5F5", ["CoverMutedBrush"] = "#B4B4BA",
                ["RuleBrush"] = "#36363B", ["HairlineBrush"] = "#303034", ["LineBrush"] = "#36363B",
                ["SealBrush"] = "#559CF0", ["AccentBrush"] = "#559CF0", ["SealSoftBrush"] = "#283A4E",
                ["AccentSoftBrush"] = "#283A4E", ["CyanBrush"] = "#80BCFF", ["PrimaryTextBrush"] = "#FFFFFF",
                ["WarnSoftBrush"] = "#382A22", ["DangerBrush"] = "#FF8E97",
            }
            : new Dictionary<string, string>
            {
                ["PaperBrush"] = "#F6F7F9", ["SheetBrush"] = "#FFFFFF", ["CoverBrush"] = "#EBEDF1",
                ["InkBrush"] = "#1E2229", ["BodyBrush"] = "#30343A", ["MutedBrush"] = "#6B707A",
                ["FaintBrush"] = "#737984", ["CoverTextBrush"] = "#1E2229", ["CoverMutedBrush"] = "#626872",
                ["RuleBrush"] = "#D8DBE1", ["HairlineBrush"] = "#E4E6EA", ["LineBrush"] = "#D8DBE1",
                ["SealBrush"] = "#367DCE", ["AccentBrush"] = "#367DCE", ["SealSoftBrush"] = "#DDEBFC",
                ["AccentSoftBrush"] = "#DDEBFC", ["CyanBrush"] = "#286FB5", ["PrimaryTextBrush"] = "#FFFFFF",
                ["WarnSoftBrush"] = "#FFF1E5", ["DangerBrush"] = "#B33B48",
            };
        if (SystemParameters.HighContrast) return;
        foreach (var pair in colors)
            window.Resources[pair.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
    }

    public void Close()
    {
        _parentTimer.Stop();
        _window?.Close();
    }

    public void Dispose() => Close();

    private static IntPtr FindSettingsWindow(int processId)
    {
        var found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pid != processId || !IsWindowVisible(window)) return true;
            var title = new StringBuilder(128);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString() != "备忘设置") return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
