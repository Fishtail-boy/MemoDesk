using System.Windows;
using System.Windows.Threading;
using MemoDesk.Infrastructure.Settings;
using MemoDesk.Vault.Core;
using Microsoft.Win32;

namespace MemoDesk.App.Services;

public sealed class VaultLockCoordinator : IDisposable
{
    private readonly IVaultService _vault;
    private readonly ISecureClipboard _clipboard;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private Window? _window;
    private bool _started, _disposed;
    public VaultActivity Activity { get; }
    public VaultLockCoordinator(IVaultService vault, ISecureClipboard clipboard, AppSettings settings, TimeProvider clock)
    {
        _vault = vault; _clipboard = clipboard; _settings = settings; Activity = new VaultActivity(clock);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => { if (_vault.IsUnlocked && Activity.HasExpired(TimeSpan.FromMinutes(_settings.VaultIdleMinutes))) Lock(); };
    }
    public void Start()
    {
        if (_started) return;
        _started = true;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerChanged;
        _timer.Start();
    }
    public void Attach(Window window)
    {
        _window = window;
        window.StateChanged += WindowStateChanged;
        window.IsVisibleChanged += WindowVisibilityChanged;
    }
    private void WindowStateChanged(object? sender, EventArgs e) { if (_window?.WindowState == WindowState.Minimized) Lock(); }
    private void WindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (_window?.IsVisible == false) Lock(); }
    public void Lock()
    {
        _vault.Lock();
        if (_dispatcher.CheckAccess()) _clipboard.ClearOwned();
        else _dispatcher.BeginInvoke(_clipboard.ClearOwned);
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or
            SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.SessionRemoteControl)
            Lock();
    }
    private void OnPowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume) Lock();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop(); SystemEvents.SessionSwitch -= OnSessionSwitch; SystemEvents.PowerModeChanged -= OnPowerChanged;
        if (_window is { } window) { window.StateChanged -= WindowStateChanged; window.IsVisibleChanged -= WindowVisibilityChanged; _window = null; }
        Lock(); GC.SuppressFinalize(this);
    }
}
