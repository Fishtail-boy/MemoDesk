using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MemoDesk.App.Services;
using MemoDesk.App.ViewModels;
using MemoDesk.App.Views;
using MemoDesk.Core;
using MemoDesk.Core.Capturing;
using MemoDesk.Core.Drafting;
using MemoDesk.Core.Items;
using MemoDesk.Core.Parsing;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Settings;
using MemoDesk.Infrastructure;
using MemoDesk.Infrastructure.Parsing;
using MemoDesk.Infrastructure.Queries;
using MemoDesk.Infrastructure.Settings;
using MemoDesk.Infrastructure.Storage;
using MemoDesk.Infrastructure.Storage.Migrations;
using MemoDesk.Platform.Windows.Hotkeys;
using MemoDesk.Platform.Windows.Notifications;
using MemoDesk.Platform.Windows.Settings;
using MemoDesk.Platform.Windows.SingleInstance;
using MemoDesk.Platform.Windows.Tray;
using MemoDesk.Platform.Windows.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace MemoDesk.App;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstanceCoordinator? _singleInstance;
    private HotkeyManager? _hotkeys;
    private TrayIconService? _tray;
    private ReminderPresenter? _reminderPresenter;
    private ReminderScheduleCoordinator? _scheduleCoordinator;
    private DispatcherTimer? _scheduleTimer;
    private WindowsNotificationGateway? _notificationGateway;
    private DispatcherTimer? _reminderTimer;
    private ReminderWakeService? _reminderWake;
    private MemoWidgetWindow? _widget;
    private MainViewModel? _mainViewModel;
    private StartupMode _startupMode = StartupMode.Normal;
    private ReminderNotificationAction? _pendingOpenAction;
    private bool _hostStarted;
    private bool _isExiting;

    private enum StartupMode
    {
        Normal,
        NotificationAction,
    }

    /// <summary>是否正在执行真正的退出流程（用于区分“关闭窗口”与“退出应用”）。</summary>
    internal bool IsExiting => _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        AppTheme.Start();
        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();

        _singleInstance = new SingleInstanceCoordinator();
        var activation = NotificationActivationReader.TryRead();
        if (!_singleInstance.TryAcquirePrimary())
        {
            var command = activation is null
                ? AppCommands.Activate
                : AppCommands.ForNotificationAction(activation);
            await SingleInstanceCoordinator.TrySendCommandAsync(command, TimeSpan.FromSeconds(3));
            Shutdown();
            return;
        }

        try
        {
            if (activation is { Action: "complete" or "snooze" })
            {
                _startupMode = StartupMode.NotificationAction;
            }
            else if (activation is not null)
            {
                _pendingOpenAction = activation;
            }

            _host = BuildHost();

            // 数据库必须在后台解析等托管服务启动前完成迁移。
            var bootstrapper = _host.Services.GetRequiredService<DatabaseBootstrapper>();
            bootstrapper.Initialize();

            if (_startupMode == StartupMode.NotificationAction && activation is not null)
            {
                RunNotificationActionMode(activation);
                return;
            }

            await _host.StartAsync();
            _hostStarted = true;

            var paths = _host.Services.GetRequiredService<AppPaths>();
            _host.Services.GetRequiredService<ILogger<App>>().LogInformation(
                "应用启动完成。数据库：{DatabasePath}；结构版本：{SchemaVersion}；系统：{OSVersion}",
                paths.DatabasePath,
                bootstrapper.GetSchemaVersion(),
                Environment.OSVersion.VersionString);

            ConfigureMainWindow();
            ConfigureTray();
            ConfigureHotkey();
            ConfigureQuickCapture();
            ConfigureParsing();
            ConfigureReminders();
            ConfigureNotifications();
            ConfigureScheduleCoordinator();
            ConfigureWidget();
            ConfigureItemChangeEvents();

            _singleInstance.StartListening(HandleCommandAsync);
            ShowMainWindow();

            // 未打包/通知网关不可用时，冷启动的正文激活无法通过 NotificationInvoked 回放。
            if (_pendingOpenAction is not null && _notificationGateway?.IsAvailable != true)
            {
                _reminderPresenter?.HandleAction(_pendingOpenAction);
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "应用启动失败");
            _isExiting = true;
            MessageBox.Show(
                $"应用启动失败：{ex.Message}\n\n日志目录：{AppPaths.ForCurrentUser().LogDirectory}",
                AppDefaults.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _reminderTimer?.Stop();
            _reminderWake?.Dispose();
            _reminderWake = null;
            Microsoft.Win32.SystemEvents.TimeChanged -= OnClockChanged;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _reminderTimer = null;

            if (_startupMode == StartupMode.Normal)
            {
                try
                {
                    _host?.Services.GetService<DraftsViewModel>()?.PersistEdits();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "退出前保存草稿编辑失败");
                }
            }

            _scheduleTimer?.Stop();
            _scheduleTimer = null;

            if (_startupMode == StartupMode.Normal)
            {
                try
                {
                    _scheduleCoordinator?.RevokeAllScheduled();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "退出前撤销计划通知失败");
                }
            }

            _scheduleCoordinator = null;

            if (_startupMode == StartupMode.Normal)
            {
                _notificationGateway?.Dispose();
            }
            else
            {
                // 动作模式刚登记了下一次系统通知，注销通知平台可能连带影响它；
                // 进程即将结束，句柄由系统回收即可。
                _notificationGateway = null;
            }

            _hotkeys = null;

            _tray?.Dispose();
            _tray = null;

            _singleInstance?.Dispose();
            _singleInstance = null;

            if (_host is not null)
            {
                if (_hostStarted)
                {
                    _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                }

                _host.Dispose();
                _host = null;
            }
        }
        finally
        {
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }

    private static IHost BuildHost()
    {
        var paths = AppPaths.ForCurrentUser();
        paths.EnsureDirectories();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogDirectory, "memo-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: false,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        return new HostBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddSingleton(paths);
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton(new SqliteConnectionFactory(paths.DatabasePath));
                services.AddSingleton<MigrationRunner>();
                services.AddSingleton<DatabaseBootstrapper>();
                services.AddSingleton<LegacyMemoMigration>();

                services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
                services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
                services.AddSingleton<AppSettings>();

                services.AddSingleton<ICaptureRepository, SqliteCaptureRepository>();
                services.AddSingleton<IMemoCaptureStore, SqliteMemoCaptureStore>();
                services.AddSingleton<IParseDraftRepository, SqliteParseDraftRepository>();
                services.AddSingleton<IMemoParseRepository, SqliteMemoParseRepository>();
                services.AddSingleton<IMemoApplyStore, SqliteMemoApplyStore>();
                services.AddSingleton<IItemStore, SqliteItemStore>();
                services.AddSingleton<IReminderRepository, SqliteReminderRepository>();

                services.AddSingleton<CaptureService>();
                services.AddSingleton<MemoCaptureService>();
                services.AddSingleton<ItemService>();
                services.AddSingleton<ReminderScheduler>();
                services.AddSingleton<DraftConfirmationService>();
                services.AddSingleton<ItemQueryService>();
                services.AddSingleton<ReminderQueryService>();
                services.AddSingleton<SearchService>();

                services.AddSingleton<IAiParser, DeepSeekParser>();
                services.AddSingleton<IMemoAiParser>(provider => (DeepSeekParser)provider.GetRequiredService<IAiParser>());
                services.AddSingleton<ParseCoordinator>();
                services.AddSingleton<MemoParseCoordinator>();
                services.AddHostedService<ParseWorkerService>();

                services.AddSingleton<WindowForegroundTracker>();
                services.AddSingleton<MonitorLocator>();
                services.AddSingleton<GlobalHotkeyService>();
                services.AddSingleton<HotkeyStatus>();
                services.AddSingleton<HotkeyManager>();
                services.AddSingleton<TrayIconService>();
                services.AddSingleton<WindowsNotificationGateway>();
                services.AddSingleton<IReminderNotificationGateway>(
                    provider => provider.GetRequiredService<WindowsNotificationGateway>());

                services.AddSingleton<DialogService>();
                services.AddSingleton<ReminderPresenter>();
                services.AddSingleton<ReminderScheduleCoordinator>();
                services.AddSingleton<ReminderWakeService>();

                services.AddSingleton<TodayViewModel>();
                services.AddSingleton<DraftsViewModel>();
                services.AddSingleton<TodosViewModel>();
                services.AddSingleton<RemindersViewModel>();
                services.AddSingleton<WorkLogsViewModel>();
                services.AddSingleton<MemosViewModel>();
                services.AddSingleton<SearchViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<MainViewModel>();

                services.AddSingleton<MainWindow>();
                services.AddSingleton<QuickCaptureWindow>();
                services.AddSingleton<MemoWidgetWindow>();
            })
            .Build();
    }

    private void ConfigureMainWindow()
    {
        _mainViewModel = _host!.Services.GetRequiredService<MainViewModel>();
        var window = _host.Services.GetRequiredService<MainWindow>();
        window.Closing += OnMainWindowClosing;
    }

    private void ConfigureTray()
    {
        _tray = _host!.Services.GetRequiredService<TrayIconService>();

        _tray.ShowMainWindowRequested += (_, _) => Dispatcher.InvokeAsync(() => ShowMainWindow("memos"));
        _tray.QuickCaptureRequested += (_, _) => Dispatcher.InvokeAsync(ShowQuickCapture);
        _tray.SettingsRequested += (_, _) => Dispatcher.InvokeAsync(() => ShowMainWindow("settings"));
        _tray.ToggleWidgetRequested += (_, _) => Dispatcher.InvokeAsync(() => _widget?.ToggleVisibility());
        _tray.ExitRequested += (_, _) => Dispatcher.InvokeAsync(ExitApplication);

        var hotkeyStatus = _host!.Services.GetRequiredService<HotkeyStatus>();
        _tray.SetHotkeyText(hotkeyStatus.DisplayText);
        hotkeyStatus.PropertyChanged += (_, _) =>
            Dispatcher.InvokeAsync(() => _tray?.SetHotkeyText(hotkeyStatus.DisplayText));
    }

    private void ConfigureHotkey()
    {
        _hotkeys = _host!.Services.GetRequiredService<HotkeyManager>();
        _hotkeys.CaptureRequested += () => Dispatcher.InvokeAsync(ShowQuickCapture);

        var error = _hotkeys.Start();
        if (error is null)
        {
            Log.Information("全局快捷键已注册：{Hotkey}", _hotkeys.CurrentGesture);
            return;
        }

        Log.Warning("全局快捷键注册失败：{Error}", error);
        MessageBox.Show(
            $"{error}\n\n仍然可以从托盘菜单的“记一条”打开输入框，或在设置中更换快捷键。",
            AppDefaults.ProductName,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ConfigureQuickCapture()
    {
        var window = _host!.Services.GetRequiredService<QuickCaptureWindow>();
        var coordinator = _host.Services.GetRequiredService<MemoParseCoordinator>();
        var today = _host.Services.GetRequiredService<TodayViewModel>();
        var memos = _host.Services.GetRequiredService<MemosViewModel>();

        today.QuickCaptureRequested += () => Dispatcher.InvokeAsync(ShowQuickCapture);
        memos.QuickCaptureRequested += () => Dispatcher.InvokeAsync(ShowQuickCapture);
        memos.WidgetRequested += () => Dispatcher.InvokeAsync(() => _widget?.ToggleVisibility());

        window.Captured += capture =>
        {
            var localTime = TimeZoneInfo.ConvertTime(capture.CapturedAtUtc, _host!.Services.GetRequiredService<AppSettings>().TimeZone);
            _tray?.SetStatusText($"已记录 {localTime:HH:mm}");
            _mainViewModel?.RefreshAfterMemoChange();
            _widget?.Refresh();
            _scheduleCoordinator?.Reconcile();
            _reminderWake?.Wake();
            coordinator.Notify(capture.Id);
        };
    }

    private void ConfigureParsing()
    {
        var coordinator = _host!.Services.GetRequiredService<MemoParseCoordinator>();
        coordinator.MemoChanged += _ => Dispatcher.InvokeAsync(() =>
        {
            _mainViewModel?.RefreshAfterMemoChange();
            _widget?.Refresh();
            _scheduleCoordinator?.Reconcile();
            _reminderWake?.Wake();
        });

        // 启动恢复可能已经完成了一轮解析，补一次界面刷新。
        Dispatcher.InvokeAsync(
            () =>
            {
                _mainViewModel?.RefreshAfterMemoChange();
                _mainViewModel?.NavigateTo("memos");
            },
            DispatcherPriority.Background);
    }

    private void ConfigureReminders()
    {
        _reminderPresenter = _host!.Services.GetRequiredService<ReminderPresenter>();
        _reminderPresenter.ItemsChanged += () => Dispatcher.InvokeAsync(() =>
        {
            _mainViewModel?.RefreshCurrent();
            _widget?.Refresh();

            // 完成/稍后提醒会改变计划，立即对账而不是等 60 秒轮询。
            _scheduleCoordinator?.Reconcile();
            _reminderWake?.Wake();
        });
        _reminderPresenter.OpenItemRequested += itemId =>
            Dispatcher.InvokeAsync(() => OpenItemDetail(itemId));

        _reminderWake = _host.Services.GetRequiredService<ReminderWakeService>();
        _reminderWake.Due += due => Dispatcher.InvokeAsync(() => _reminderPresenter?.CheckDue(due));
        _reminderWake.Wake();
        Microsoft.Win32.SystemEvents.TimeChanged += OnClockChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _reminderTimer.Tick += (_, _) => _reminderPresenter?.CheckDue();
        _reminderTimer.Start();

        // 启动后立即补发错过的提醒。
        Dispatcher.InvokeAsync(() => _reminderPresenter?.CheckDue(), DispatcherPriority.Background);
    }

    private void ConfigureWidget()
    {
        _widget = _host!.Services.GetRequiredService<MemoWidgetWindow>();
        var settings = _host.Services.GetRequiredService<AppSettings>();

        _widget.VisibilityChanged += () =>
        {
            _tray?.SetWidgetVisible(_widget.IsVisible);
            _host.Services.GetRequiredService<SettingsViewModel>().ShowDesktopWidget = _widget.IsVisible;
        };
        _widget.ItemsChanged += () => _mainViewModel?.RefreshCurrent();
        _widget.OpenMainRequested += () => Dispatcher.InvokeAsync(() => ShowMainWindow("memos"));

        if (settings.Widget.Visible)
        {
            _widget.ShowWidget();
        }
        _host.Services.GetRequiredService<SettingsViewModel>().SettingsSaved += () =>
        {
            if (_widget is null) return;
            if (settings.Widget.Visible) _widget.ShowWidget(); else _widget.HideWidget();
        };
    }

    private void ConfigureNotifications()
    {
        _notificationGateway = _host!.Services.GetRequiredService<WindowsNotificationGateway>();
        _notificationGateway.ActionInvoked += action =>
            Dispatcher.InvokeAsync(() => _reminderPresenter?.HandleAction(action));

        var error = _notificationGateway.Initialize();
        if (error is null)
        {
            Log.Information("Windows 系统通知已注册（计划通知可用：{Scheduling}）", _notificationGateway.IsSchedulingSupported);
        }
        else
        {
            Log.Warning("Windows 系统通知不可用，提醒将使用应用内窗口：{Error}", error);
        }
    }

    private void ConfigureScheduleCoordinator()
    {
        _scheduleCoordinator = _host!.Services.GetRequiredService<ReminderScheduleCoordinator>();
        _scheduleCoordinator.Reconcile();

        var settingsViewModel = _host.Services.GetRequiredService<SettingsViewModel>();
        settingsViewModel.SettingsSaved += () =>
            Dispatcher.InvokeAsync(() =>
            {
                _scheduleCoordinator?.Reconcile();
                _reminderWake?.Wake();
                _reminderPresenter?.CheckDue();
            });

        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _scheduleTimer.Tick += (_, _) => _scheduleCoordinator?.Reconcile();
        _scheduleTimer.Start();
    }

    private void ConfigureItemChangeEvents()
    {
        var items = _host!.Services.GetRequiredService<ItemService>();
        items.ItemChanged += () => Dispatcher.InvokeAsync(() =>
        {
            _mainViewModel?.RefreshCurrent();
            _widget?.Refresh();
            _scheduleCoordinator?.Reconcile();
            _reminderWake?.Wake();
        });
    }

    private void OnClockChanged(object? sender, EventArgs e) => _reminderWake?.Wake();
    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            Dispatcher.InvokeAsync(() => { _scheduleCoordinator?.Reconcile(); _reminderWake?.Wake(); _reminderPresenter?.CheckDue(); });
    }

    private Task HandleCommandAsync(string command)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            if (AppCommands.TryParseNotificationAction(command, out var action))
            {
                _reminderPresenter?.HandleAction(action);
                return;
            }

            switch (command)
            {
                case AppCommands.Activate:
                    ShowMainWindow("memos");
                    break;
                default:
                    Log.Warning("收到未知的启动命令：{Command}", command);
                    break;
            }
        }).Task;
    }

    private void ShowMainWindow(string? navigateTo = null)
    {
        if (_host is null)
        {
            return;
        }

        if (navigateTo is not null)
        {
            _mainViewModel?.NavigateTo(navigateTo);
        }
        else
        {
            _mainViewModel?.RefreshCurrent();
        }

        var window = _host.Services.GetRequiredService<MainWindow>();
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void ShowQuickCapture()
    {
        if (_host is null)
        {
            return;
        }

        _host.Services.GetRequiredService<QuickCaptureWindow>().ShowForCapture();
    }

    /// <summary>点击通知正文：唤起主窗口并打开对应事项的编辑窗口。</summary>
    private void OpenItemDetail(Guid itemId)
    {
        if (_host is null || _startupMode != StartupMode.Normal)
        {
            return;
        }

        ShowMainWindow("memos");

        var item = _host.Services.GetRequiredService<ItemService>().Get(itemId);
        if (item is null)
        {
            return;
        }

        var settings = _host.Services.GetRequiredService<AppSettings>();
        _host.Services.GetRequiredService<DialogService>().EditItem(item, settings.TimeZone);
    }

    /// <summary>
    /// 通知按钮冷启动：不启动完整后台/界面，只落库动作并重新登记计划通知后静默退出。
    /// </summary>
    private void RunNotificationActionMode(ReminderNotificationAction action)
    {
        _reminderPresenter = _host!.Services.GetRequiredService<ReminderPresenter>();
        _notificationGateway = _host.Services.GetRequiredService<WindowsNotificationGateway>();
        _notificationGateway.ActionInvoked += invoked =>
            Dispatcher.InvokeAsync(() => _reminderPresenter?.HandleAction(invoked));
        _notificationGateway.PreserveScheduledOnDispose = true;

        var error = _notificationGateway.Initialize(replayActivation: false);
        if (error is not null)
        {
            Log.Warning("通知动作模式下初始化通知网关失败：{Error}", error);
        }

        _reminderPresenter.HandleAction(action);

        // “稍后提醒”会产生新的计划版本，必须在退出前登记下一次通知。
        _scheduleCoordinator = _host.Services.GetRequiredService<ReminderScheduleCoordinator>();
        try
        {
            _scheduleCoordinator.Reconcile();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "通知动作模式下重新登记计划通知失败");
        }

        _isExiting = true;
        Shutdown();
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        // 关闭主窗口仅隐藏，进程、托盘、快捷键与后台任务继续常驻（设计 2.6）。
        if (sender is Window window)
        {
            e.Cancel = true;
            window.Hide();
        }
    }

    private void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        Shutdown();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "UI 线程未处理异常");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                Log.Fatal(exception, "进程未处理异常");
            }

            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "未观察的任务异常");
            args.SetObserved();
        };
    }
}
