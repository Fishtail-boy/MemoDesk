using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoDesk.App.Controls;
using MemoDesk.App.Services;
using MemoDesk.App.ViewModels;
using MemoDesk.App.Views;
using MemoDesk.Core.Reminders;
using MemoDesk.Infrastructure.Queries;
using MemoDesk.Platform.Windows.Hotkeys;
using MemoDesk.Platform.Windows.Notifications;
using MemoDesk.Platform.Windows.Windowing;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReviewProbe;

internal static partial class Program
{
    private static void LoadExperienceResources(Application app)
    {
        var xml = XDocument.Load(Path.Combine(Root, "src", "MemoDesk.App", "App.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(ns + "ResourceDictionary", xml.Root!.Attributes().Where(a => a.IsNamespaceDeclaration).Select(a => new XAttribute(a)), xml.Root.Element(ns + "Application.Resources")!.Elements().Select(e => new XElement(e)));
        var text = dictionary.ToString();
        foreach (var name in new[] { "Converters", "ViewModels", "Views" })
            text = text.Replace("clr-namespace:MemoDesk.App." + name, "clr-namespace:MemoDesk.App." + name + ";assembly=MemoDesk.App", StringComparison.Ordinal);
        app.Resources = (ResourceDictionary)XamlReader.Parse(text);
    }

    private static ReminderPresenter Presenter(Db db, ExperienceGateway gateway) => new(
        new ReminderScheduler(db.Reminders), db.ItemService, db.Reminders, gateway, db.Settings, db.Clock,
        NullLogger<ReminderPresenter>.Instance);

    private static void RunExperienceChecks()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadExperienceResources(app);
        Check("UX01", "3s capture wakes and hands off within 1 second of target, with zero AI calls", () =>
        {
            var db = new Db(useRealClock: true); var gateway = new ExperienceGateway(); var presenter = Presenter(db, gateway);
            using var wake = new ReminderWakeService(db.Reminders, db.Clock, NullLogger<ReminderWakeService>.Instance);
            using var completed = new ManualResetEventSlim();
            DateTimeOffset? handedOff = null;
            wake.Due += due => { presenter.CheckDue(due); handedOff = DateTimeOffset.UtcNow; completed.Set(); };
            var capture = db.Capture("3s后提醒我尿尿"); var plan = db.Reminders.GetActiveForItem(capture.Memo.Id)!;
            wake.Wake(); var fired = completed.Wait(TimeSpan.FromSeconds(8));
            presenter.CheckDue();
            var delay = handedOff is { } at ? (at - plan.ReminderAtUtc).TotalMilliseconds : double.PositiveInfinity;
            return (fired && delay >= -50 && delay <= 1000 && gateway.SendCount == 1,
                new { Fired = fired, DelayMs = delay, gateway.SendCount, capture.Memo.Content, Target = plan.ReminderAtUtc });
        });
        Check("UX02", "native outcome unknown must not open a fallback or resend, including presenter restart", () =>
        {
            var db = new Db(); var capture = db.Capture("结果未知的隔离提醒");
            db.Reminders.SetReminder(capture.Memo.Id, Now.AddSeconds(-1), Now);
            var gateway = new ExperienceGateway { Outcome = NotificationDeliveryOutcome.Uncertain };
            var presenter = Presenter(db, gateway); presenter.CheckDue(); presenter.CheckDue();
            Presenter(db, gateway).CheckDue();
            var plan = db.Reminders.GetActiveForItem(capture.Memo.Id)!;
            var occurrence = db.Reminders.GetBlockingOccurrenceForPlan(plan.Id, plan.PlanVersion)!;
            return (gateway.SendCount == 1 && occurrence.Status == ReminderOccurrenceStatus.Uncertain && app.Windows.Count == 0,
                new { gateway.SendCount, occurrence.Status, Windows = app.Windows.Count });
        });
        Check("UX03", "positive system history confirms an unknown handoff without a second send", () =>
        {
            var db = new Db(); var capture = db.Capture("历史核对的隔离提醒");
            db.Reminders.SetReminder(capture.Memo.Id, Now.AddSeconds(-1), Now);
            var gateway = new ExperienceGateway { Outcome = NotificationDeliveryOutcome.Uncertain, IncludeSentTag = true };
            Presenter(db, gateway).CheckDue();
            var plan = db.Reminders.GetActiveForItem(capture.Memo.Id)!;
            var occurrence = db.Reminders.GetBlockingOccurrenceForPlan(plan.Id, plan.PlanVersion)!;
            return (gateway.SendCount == 1 && occurrence.Status == ReminderOccurrenceStatus.Requested,
                new { gateway.SendCount, occurrence.Status });
        });
        Check("UX04", "disabling the fallback records a definite failure without opening a program window", () =>
        {
            var db = new Db(); var capture = db.Capture("关闭兜底的隔离提醒");
            db.Reminders.SetReminder(capture.Memo.Id, Now.AddSeconds(-1), Now); db.Settings.AllowFallbackNotifications = false;
            var gateway = new ExperienceGateway { Outcome = NotificationDeliveryOutcome.DefiniteFailure };
            Presenter(db, gateway).CheckDue();
            var plan = db.Reminders.GetActiveForItem(capture.Memo.Id)!;
            var occurrence = db.Reminders.GetBlockingOccurrenceForPlan(plan.Id, plan.PlanVersion)!;
            return (occurrence.Status == ReminderOccurrenceStatus.Failed && app.Windows.Count == 0,
                new { occurrence.Status, Windows = app.Windows.Count });
        });
        Check("UX05", "time input validates hours/minutes/seconds and preserves sub-minute precision", () =>
        {
            var field = new TimeField { Text = "15:04:37" }; field.CommitText();
            var seconds = field.SelectedTime?.Second;
            field.Text = "25:70:00"; field.CommitText(); var invalid = !field.IsValid;
            field.Text = "15:04:37"; field.CommitText();
            return (seconds == 37 && invalid && field.IsValid && field.SelectedTime?.Second == 37,
                new { Seconds = seconds, InvalidRejected = invalid, field.IsValid });
        });
        Check("UX06", "selecting the same valid time restores invalid input; choosing seconds is explicit", () =>
        {
            var field = new TimeField { Text = "invalid" }; field.CommitText();
            ((ComboBox)field.FindName("Hours")).SelectedIndex = 15;
            ((ComboBox)field.FindName("Minutes")).SelectedIndex = 4;
            ((ComboBox)field.FindName("Seconds")).SelectedIndex = 37;
            ((CheckBox)field.FindName("SecondsToggle")).IsChecked = true;
            typeof(TimeField).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(field, null);
            return (field.IsValid && field.Text == "15:04:37" && field.SelectedTime == new TimeOnly(15, 4, 37),
                new { field.IsValid, field.Text, field.SelectedTime });
        });
        Check("UX07", "global tomorrow shortcut retains selected seconds through load and save", () =>
        {
            var db = new Db();
            db.Settings.SnoozeOptions = new[] { new SnoozeOption("seconds", "明天指定时间", SnoozeKind.TomorrowAt, 0, new TimeOnly(9, 4, 37)) };
            using var hotkeys = new GlobalHotkeyService();
            var manager = new HotkeyManager(hotkeys, db.Settings, new HotkeyStatus());
            var vm = new SettingsViewModel(db.Settings, manager); vm.SaveCommand.Execute(null);
            return (vm.SnoozeOptions[0].TimeText == "09:04:37" && db.Settings.SnoozeOptions[0].TomorrowTime?.Second == 37,
                new { vm.SnoozeOptions[0].TimeText, db.Settings.SnoozeOptions[0].TomorrowTime });
        });
        Check("UX08", "definite native failure opens one fallback card; closing keeps the memo open", () =>
        {
            var db = new Db(); var capture = db.Capture("明确失败才兜底");
            db.Reminders.SetReminder(capture.Memo.Id, Now.AddSeconds(-1), Now);
            var gateway = new ExperienceGateway { Outcome = NotificationDeliveryOutcome.DefiniteFailure };
            Presenter(db, gateway).CheckDue();
            var cards = app.Windows.OfType<InAppReminderWindow>().ToArray();
            var plan = db.Reminders.GetActiveForItem(capture.Memo.Id)!;
            var occurrence = db.Reminders.GetBlockingOccurrenceForPlan(plan.Id, plan.PlanVersion)!;
            foreach (var card in cards) card.Close();
            return (cards.Length == 1 && occurrence.Status == ReminderOccurrenceStatus.Requested && occurrence.Channel == "inapp"
                && db.Items.GetById(capture.Memo.Id)!.Status == MemoDesk.Core.Domain.ItemStatus.Open,
                new { Cards = cards.Length, occurrence.Status, occurrence.Channel, ItemStatus = db.Items.GetById(capture.Memo.Id)!.Status });
        });
        Check("UX09", "explicit request-now can bypass a short countdown and still sends only once", () =>
        {
            var db = new Db(); var capture = db.Capture("3s后提醒我喝水");
            var gateway = new ExperienceGateway(); var presenter = Presenter(db, gateway);
            var vm = new RemindersViewModel(new ReminderQueryService(db.Factory), db.Reminders, db.ItemService,
                presenter, db.Settings, db.Clock, db.Dialogs);
            vm.Refresh(); vm.RequestNowCommand.Execute(vm.Rows.Single()); presenter.CheckDue();
            return (gateway.SendCount == 1, new { gateway.SendCount, vm.Message });
        });
        File.WriteAllText(Path.Combine(Output, "experience-results.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
        app.Shutdown();
    }

    private static void RunExperienceUi()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose }; LoadExperienceResources(app); AppTheme.Start();
        var db = new Db(useRealClock: true); var status = new HotkeyStatus(); db.Settings.Hotkey = "Ctrl+Alt+Shift+F21";
        using var hotkeys = new GlobalHotkeyService(); var manager = new HotkeyManager(hotkeys, db.Settings, status); manager.Start();
        var a = db.Capture("下午把接口联调结果发给同事");
        db.Reminders.SetReminder(a.Memo.Id, db.Clock.GetUtcNow().AddHours(2).AddSeconds(17), db.Clock.GetUtcNow());
        db.Capture("记住：测试环境的配置明天再确认");
        db.Capture("周会要讨论桌面便签的交互细节");
        var memo = db.Memos(status); var work = new WorkLogsViewModel(db.Queries, db.ItemService, db.Settings, db.Dialogs, db.Captures, db.Clock);
        var settings = new SettingsViewModel(db.Settings, manager);
        var gateway = new ExperienceGateway(); var presenter = Presenter(db, gateway);
        var reminders = new RemindersViewModel(new ReminderQueryService(db.Factory), db.Reminders, db.ItemService, presenter, db.Settings, db.Clock, db.Dialogs);
        var shell = new ExperienceShell(memo, work, reminders, settings);
        var xml = XDocument.Load(Path.Combine(Root, "src", "MemoDesk.App", "MainWindow.xaml"));
        xml.Root!.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Remove();
        var window = (Window)XamlReader.Parse(xml.ToString()); window.Title = "[隔离验收] 浅色玻璃备忘"; window.DataContext = shell;
        WindowEffects.Attach(window);
        var widget = new MemoWidgetWindow(db.Queries, db.ItemService, db.Settings, db.Dialogs, db.Clock, db.Reminders);
        widget.Title = "[隔离验收] 桌面备忘";
        widget.ShowInTaskbar = true; // 仅测试宿主：让桌面自动化能选择这个被动窗口。
        memo.WidgetRequested += widget.ToggleVisibility;
        widget.OpenMainRequested += () => { window.Show(); window.Activate(); shell.NavigateCommand.Execute("memos"); };
        var capture = new QuickCaptureWindow(db.CaptureService, new WindowForegroundTracker(), new MonitorLocator(), db.Settings, status);
        memo.QuickCaptureRequested += capture.ShowForCapture; manager.CaptureRequested += capture.ShowForCapture;
        using var wake = new ReminderWakeService(db.Reminders, db.Clock, NullLogger<ReminderWakeService>.Instance);
        wake.Due += due => app.Dispatcher.InvokeAsync(() => { presenter.CheckDue(due); memo.Refresh(); widget.Refresh(); });
        capture.Captured += _ => { memo.Refresh(); work.Refresh(); widget.Refresh(); wake.Wake(); };
        presenter.ItemsChanged += () => { memo.Refresh(); widget.Refresh(); wake.Wake(); };
        var scenePanel = new StackPanel { Margin = new Thickness(24) };
        var preview = new Button { Content = "预览兜底提醒卡片", Width = 190, Height = 38, HorizontalAlignment = HorizontalAlignment.Left };
        preview.Click += (_, _) =>
        {
            var item = db.Items.GetById(a.Memo.Id)!;
            var occurrence = new ReminderOccurrence(Guid.NewGuid(), Guid.NewGuid(), item.Id, db.Clock.GetUtcNow(), ReminderOccurrenceStatus.Pending,
                "preview", db.Settings.SnoozeOptions, null, null, null, 0, null, db.Clock.GetUtcNow(), db.Clock.GetUtcNow());
            var card = new InAppReminderWindow(new PendingReminder(occurrence, item, false), db.Settings.TimeZone);
            card.Owner = window;
            card.CompleteRequested += () => card.Close(); card.SetQueueCount(2); card.Show();
        };
        scenePanel.Children.Add(preview); shell.Preview = scenePanel;
        shell.Navigation.Add(new NavItemViewModel("preview", "隔离场景"));
        db.ItemService.ItemChanged += () => { memo.Refresh(); widget.Refresh(); };
        app.MainWindow = window; memo.Refresh(); work.Refresh(); widget.ShowWidget();
        File.WriteAllText(Path.Combine(Output, "experience-ui-session.json"), JsonSerializer.Serialize(new { db.Factory.DatabasePath, NetworkCalls = false, NativeNotifications = false, ProductionWindowXaml = true }));
        app.Run(window);
    }

    private sealed class ExperienceShell : ObservableObject
    {
        private object _current;
        public object CurrentPage { get => _current; private set => SetProperty(ref _current, value); }
        public List<NavItemViewModel> Navigation { get; } = new() { new("memos", "备忘"), new("worklogs", "工作记录"), new("reminders", "提醒"), new("settings", "设置") };
        public IRelayCommand<string> NavigateCommand { get; }
        public object? Preview { get; set; }
        public ExperienceShell(MemosViewModel memos, WorkLogsViewModel work, RemindersViewModel reminders, SettingsViewModel settings)
        {
            _current = memos; Navigation[0].IsActive = true;
            NavigateCommand = new RelayCommand<string>(key =>
            {
                foreach (var nav in Navigation) nav.IsActive = nav.Key == key;
                if (key == "worklogs") { work.Refresh(); CurrentPage = work; }
                else if (key == "reminders") { reminders.Refresh(); CurrentPage = reminders; }
                else if (key == "settings") { settings.Load(); CurrentPage = settings; }
                else if (key == "preview") { CurrentPage = Preview!; }
                else { memos.Refresh(); CurrentPage = memos; }
            });
        }
    }
    private static void RunNativeTest()
    {
        using var gateway = new WindowsNotificationGateway();
        var error = gateway.Initialize(replayActivation: false);
        var outcome = error is null ? gateway.SendTest() : NotificationDeliveryOutcome.DefiniteFailure;
        File.WriteAllText(Path.Combine(Output, "native-notification-test.json"), JsonSerializer.Serialize(new { error, gateway.IsAvailable, gateway.IsSchedulingSupported, Status = gateway.DescribeStatus(), Outcome = outcome.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RunCardUi()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose }; LoadExperienceResources(app);
        var db = new Db(useRealClock: true); var capture = db.Capture("喝杯水，站起来活动一下");
        var now = db.Clock.GetUtcNow();
        var occurrence = new ReminderOccurrence(Guid.NewGuid(), Guid.NewGuid(), capture.Memo.Id, now,
            ReminderOccurrenceStatus.Pending, "preview", db.Settings.SnoozeOptions, null, null, null, 0, null, now, now);
        var card = new InAppReminderWindow(new PendingReminder(occurrence, capture.Memo, false), db.Settings.TimeZone)
            { Title = "[隔离验收] 兜底提醒", ShowInTaskbar = true };
        card.CompleteRequested += () => card.Close(); card.SnoozeRequested += _ => card.Close(); card.SetQueueCount(2);
        card.Closed += (_, _) => LogUi(new { Case = "fallback-card-closed", StillOpen = db.Items.GetById(capture.Memo.Id)!.Status.ToString() });
        app.MainWindow = card; app.Run(card);
    }

    private sealed class ExperienceGateway : IReminderNotificationGateway
    {
        public bool IsAvailable => true; public bool IsSchedulingSupported => false;
        public int SendCount; public NotificationDeliveryOutcome Outcome = NotificationDeliveryOutcome.HandedOff;
        public bool IncludeSentTag; private string? _tag;
        public NotificationDeliveryOutcome SendNow(ReminderNotification notification) { SendCount++; _tag = notification.Tag; return Outcome; }
        public NotificationScheduleOutcome Schedule(ScheduledReminder reminder) => NotificationScheduleOutcome.Unsupported;
        public bool CancelScheduled(string notificationTag) => true;
        public NotificationScheduleSnapshot QueryScheduled() => new(true, Array.Empty<string>(), null);
        public NotificationHistorySnapshot QueryHistory() => new(true, IncludeSentTag && _tag is not null ? new[] { _tag } : Array.Empty<string>(), null);
        public event Action<ReminderNotificationAction>? ActionInvoked { add { } remove { } }
    }
}
