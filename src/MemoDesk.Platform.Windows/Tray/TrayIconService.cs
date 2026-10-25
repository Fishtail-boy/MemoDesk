using System.Drawing;
using MemoDesk.Core;
using Forms = System.Windows.Forms;

namespace MemoDesk.Platform.Windows.Tray;

/// <summary>
/// 托盘常驻（设计 2.6）：左键打开备忘工作台，右键菜单提供“记一条”、桌面备忘、设置与退出。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int MaxTooltipLength = 63;

    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _widgetItem;
    private readonly Forms.ToolStripMenuItem _quickCaptureItem;

    public TrayIconService()
    {
        _quickCaptureItem = new Forms.ToolStripMenuItem("记一条");
        _quickCaptureItem.Click += (_, _) => QuickCaptureRequested?.Invoke(this, EventArgs.Empty);

        var showItem = new Forms.ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => ShowMainWindowRequested?.Invoke(this, EventArgs.Empty);

        _widgetItem = new Forms.ToolStripMenuItem("显示桌面备忘");
        _widgetItem.Click += (_, _) => ToggleWidgetRequested?.Invoke(this, EventArgs.Empty);

        var settingsItem = new Forms.ToolStripMenuItem("设置");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(_quickCaptureItem);
        _menu.Items.Add(showItem);
        _menu.Items.Add(_widgetItem);
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = Truncate(AppDefaults.ProductName),
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                ShowMainWindowRequested?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public event EventHandler? ShowMainWindowRequested;

    public event EventHandler? QuickCaptureRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? ToggleWidgetRequested;

    public event EventHandler? ExitRequested;

    public void SetHotkeyText(string gesture) =>
        _quickCaptureItem.Text = string.IsNullOrWhiteSpace(gesture) ? "记一条" : $"记一条（{gesture}）";

    public void SetWidgetVisible(bool visible) =>
        _widgetItem.Text = visible ? "隐藏桌面备忘" : "显示桌面备忘";

    /// <summary>显示一条简短状态（例如“已记录 14:32”）。</summary>
    public void SetStatusText(string? status)
    {
        _notifyIcon.Text = Truncate(string.IsNullOrWhiteSpace(status)
            ? AppDefaults.ProductName
            : $"{AppDefaults.ProductName} · {status}");
    }

    private static string Truncate(string text) =>
        text.Length <= MaxTooltipLength ? text : text[..MaxTooltipLength];

    private static Icon LoadAppIcon()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            try
            {
                var icon = Icon.ExtractAssociatedIcon(processPath);
                if (icon is not null)
                {
                    return icon;
                }
            }
            catch (ArgumentException)
            {
                // 回退到系统默认图标。
            }
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}
