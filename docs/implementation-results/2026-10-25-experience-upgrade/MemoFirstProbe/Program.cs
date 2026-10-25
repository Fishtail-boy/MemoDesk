using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using MemoDesk.App.Services;
using MemoDesk.App.ViewModels;
using MemoDesk.App.Views;
using MemoDesk.Core;
using MemoDesk.Core.Capturing;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Core.Parsing;
using MemoDesk.Core.Settings;
using MemoDesk.Infrastructure.Queries;
using MemoDesk.Infrastructure.Settings;
using MemoDesk.Infrastructure.Storage;
using MemoDesk.Infrastructure.Storage.Migrations;
using MemoDesk.Platform.Windows.Hotkeys;
using MemoDesk.Platform.Windows.Windowing;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReviewProbe;

internal static partial class Program
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);
    private static readonly List<object> Results = new();
    private static string Output = string.Empty;
    private static string Root = string.Empty;

    [STAThread]
    private static int Main(string[] args)
    {
        Root = FindRoot();
        Output = Path.Combine(Root, "docs", "implementation-results", "2026-10-25-experience-upgrade");
        try
        {
            if (args.Contains("--experience-checks")) { RunExperienceChecks(); return 0; }
            if (args.Contains("--experience-ui")) { RunExperienceUi(); return 0; }
            if (args.Contains("--native-test")) { RunNativeTest(); return 0; }
            if (args.Contains("--card-ui")) { RunCardUi(); return 0; }
            if (args.Contains("--r5-checks"))
            {
                RunR5Checks();
                File.WriteAllText(Path.Combine(Output, "r5-boundary-results.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (args.Contains("--r4-checks"))
            {
                RunR4Checks();
                File.WriteAllText(Path.Combine(Output, "r4-boundary-results.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (args.Contains("--editor")) { RunEditorUi(args); return 0; }
            if (args.Contains("--ui") || Path.GetFileName(Environment.ProcessPath ?? string.Empty).StartsWith("UiMemo", StringComparison.Ordinal)) { RunUi(); return 0; }
            RunChecks();
            RunExtraChecks();
            RunR2Checks();
            RunR3Checks();
            File.WriteAllText(Path.Combine(Output, "memo-first-probe-results.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Output, "probe-fatal.txt"), ex.ToString());
            return 2;
        }
    }

    private static void RunChecks()
    {
        Check("MF01", "immediate capture atomically creates memo and event; raw text preserved", () =>
        {
            var db = new Db(); var result = db.Capture("  synthetic-raw\n第二行  ");
            var raw = db.Captures.GetById(result.Capture.Id)!;
            return (raw.RawText == "  synthetic-raw\n第二行  " && db.Count("items") == 1 && db.Count("timeline_events") == 1,
                new { raw.RawText, Memos = db.Count("items"), Events = db.Count("timeline_events"), result.Memo.Type });
        });
        Check("MF02", "stable submission IDs do not create duplicates", () =>
        {
            var db = new Db(); var a = db.Capture("synthetic-repeat");
            var b = db.CaptureService.Capture("synthetic-repeat", "Asia/Shanghai", a.Capture.Id, a.Memo.Id);
            return (b.AlreadyExisted && db.Count("items") == 1 && db.Count("timeline_events") == 1, new { b.AlreadyExisted, Count = db.Count("items") });
        });
        Check("MF03", "AI split preserves initial ID, creates two reminders and applies only once", () =>
        {
            var db = new Db();
            var a = db.Capture("2026-10-25T11:00:00+08:00 第一件；2026-10-25T12:00:00+08:00 第二件");
            var result = db.Result(a, new[]
            {
                Candidate("first", Now.AddHours(1), "2026-10-25T11:00:00+08:00 第一件", "2026-10-25T11:00:00+08:00"),
                Candidate("second", Now.AddHours(2), "2026-10-25T12:00:00+08:00 第二件", "2026-10-25T12:00:00+08:00"),
            });
            var first = db.Apply.Apply(result.Id, Now.AddMinutes(1)); var again = db.Apply.Apply(result.Id, Now.AddMinutes(2));
            return (first == MemoApplyOutcome.Applied && again == MemoApplyOutcome.AlreadyApplied && db.Count("items") == 2 && db.Count("reminders") == 2 && db.Items.GetById(a.Memo.Id)!.Content == "first", new { first, again, Items = db.Count("items"), Reminders = db.Count("reminders") });
        });
        Check("MF04", "human edit blocks late AI and split", () =>
        {
            var db = new Db(); var a = db.Capture("synthetic-before-edit");
            db.Items.Update(a.Memo with { Content = "human-content" });
            var r = db.Result(a, new[] { Candidate("AI", Now.AddHours(1)), Candidate("AI extra", Now.AddHours(2)) });
            var status = db.Apply.Apply(r.Id, Now);
            return (db.Items.GetById(a.Memo.Id)!.Content == "human-content" && db.Count("items") == 1 && db.Count("reminders") == 0, new { status, Count = db.Count("items") });
        });
        Check("MF05", "deleted memo is not revived by late AI", () =>
        {
            var db = new Db(); var a = db.Capture("synthetic-delete"); db.Items.TrySetStatus(a.Memo.Id, ItemStatus.Deleted, Now);
            var r = db.Result(a, new[] { Candidate("late", Now.AddHours(1)) }); var outcome = db.Apply.Apply(r.Id, Now);
            return (db.Items.GetById(a.Memo.Id)!.Status == ItemStatus.Deleted && db.Count("reminders") == 0, new { outcome });
        });
        foreach (var (id, raw) in new[] { ("TM01", "记住接口字段名称"), ("TM02", "明天下午处理报表"), ("TM03", "明天十五点处理报表，不提醒") })
        {
            Check(id, "unsupported or explicitly disabled reminder must be rejected locally", () =>
            {
                var request = new AiParseRequest(raw, Now, "Asia/Shanghai", MemoParsePrompt.Version, MemoParsePrompt.SchemaVersion);
                var response = MemoResponseMapper.Map("{\"items\":[{\"content\":\"synthetic\",\"remindAt\":\"2026-10-26T15:00:00+08:00\"}]}", "synthetic-model", request);
                var db = new Db(); var a = db.Capture(raw); var r = db.Result(a, response.Candidates); db.Apply.Apply(r.Id, Now);
                return (db.Reminders.GetActiveForItem(a.Memo.Id) is null, new { Raw = raw, Accepted = response.Candidates[0].RemindAtUtc, ActivePlans = db.Count("reminders") });
            });
        }
        Check("TM04", "explicit immediate reminder can create a due plan at capture time", () =>
        {
            var request = new AiParseRequest("现在提醒我提交报表", Now, "Asia/Shanghai", MemoParsePrompt.Version, MemoParsePrompt.SchemaVersion);
            var json = JsonSerializer.Serialize(new { items = new[] { new { content = "提交报表", sourceText = "现在提醒我提交报表", remindText = "现在提醒", remindAt = Now } } });
            var response = MemoResponseMapper.Map(json, "synthetic-model", request);
            var db = new Db(); var a = db.Capture(request.RawText); db.Apply.Apply(db.Result(a, response.Candidates).Id, Now);
            return (db.Reminders.GetActiveForItem(a.Memo.Id) is not null, new { ActivePlans = db.Count("reminders"), response.Candidates[0].RemindAtUtc });
        });
        Check("ED01", "stale editor must not overwrite a newer content revision", () =>
        {
            var db = new Db(); var a = db.Capture("synthetic-version-one");
            db.Items.Update(a.Memo with { Content = "newer-editor" });
            bool rejected = false; try { db.Items.Update(a.Memo with { Content = "stale-editor" }); } catch { rejected = true; }
            var current = db.Items.GetById(a.Memo.Id)!;
            return (current.Content == "newer-editor", new { rejected, current.Content, current.RowVersion });
        });
        Check("ED02", "content and changed reminder must commit atomically", () =>
        {
            var db = new Db(); var a = db.Capture("old-content");
            db.Reminders.SetReminder(a.Memo.Id, Now.AddHours(1), Now);
            var plan = db.Reminders.GetActiveForItem(a.Memo.Id)!;
            db.Sql("CREATE TRIGGER fail_reminder_update BEFORE UPDATE ON reminders BEGIN SELECT RAISE(ABORT, 'synthetic write failure'); END;");
            var result = db.Items.UpdateWithReminder(
                a.Memo with { Content = "new-content", UpdatedAtUtc = Now },
                new ReminderEditIntent(ReminderEditKind.Set, Now.AddHours(2), plan.Id, plan.PlanVersion, true),
                Now);
            var current = db.Items.GetById(a.Memo.Id)!;
            return (!result.IsSuccess && current.Content == "old-content", new { result.Outcome, current.Content, Reminder = db.Reminders.GetActiveForItem(a.Memo.Id)?.ReminderAtUtc });
        });
        Check("HK01", "rebinding releases the startup shortcut", () =>
        {
            var db = new Db(); db.Settings.Hotkey = "Ctrl+Alt+Shift+F21";
            using var service = new GlobalHotkeyService(); using var verifier = new GlobalHotkeyService();
            var status = new HotkeyStatus(); var manager = new HotkeyManager(service, db.Settings, status);
            var start = manager.Start(); if (start is not null) throw new InvalidOperationException(start);
            var change = manager.ApplyCandidate("Ctrl+Alt+Shift+F22");
            bool oldReleased = true; string? error = null;
            try { verifier.Register("Ctrl+Alt+Shift+F21", () => { }); } catch (Exception ex) { oldReleased = false; error = ex.Message; }
            return (change is null && oldReleased, new { change, oldReleased, error, status.DisplayText });
        });
        Check("HK02", "conflicting candidate preserves the old registered display", () =>
        {
            var db = new Db(); db.Settings.Hotkey = "Ctrl+Alt+Shift+F21";
            using var service = new GlobalHotkeyService(); using var blocker = new GlobalHotkeyService();
            var status = new HotkeyStatus(); var manager = new HotkeyManager(service, db.Settings, status);
            var start = manager.Start(); if (start is not null) throw new InvalidOperationException(start);
            blocker.Register("Ctrl+Alt+Shift+F23", () => { });
            var error = manager.ApplyCandidate("Ctrl+Alt+Shift+F23");
            return (error is not null && status.IsRegistered && status.EffectiveGesture == "Ctrl+Alt+Shift+F21", new { error, status.IsRegistered, status.EffectiveGesture, status.DisplayText });
        });
        Check("RC01", "startup recovery queues all 1001 pending captures", () =>
        {
            var db = new Db();
            using (var connection = db.Factory.Create()) using (var transaction = connection.BeginTransaction())
            {
                for (var i = 0; i < 1001; i++)
                {
                    using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
                    cmd.CommandText = "INSERT INTO captures(id,raw_text,captured_at_utc,captured_time_zone_id,parse_status,recorded_at_utc,parse_operation_version) VALUES($id,'synthetic','2026-10-25T02:00:00.0000000+00:00','Asia/Shanghai','Pending','2026-10-25T02:00:00.0000000+00:00',1)";
                    cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); cmd.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            var coordinator = db.Coordinator();
            typeof(MemoParseCoordinator).GetMethod("Recover", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(coordinator, null);
            var queue = (Channel<Guid>)typeof(MemoParseCoordinator).GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
            var count = queue.Reader.Count;
            return (count == 1001, new { Pending = db.Count("captures"), Queued = count });
        });
        Check("MG01", "legacy pending capture does not enable a historical reminder after delayed AI", () =>
        {
            var db = new Db(); var capturedAt = Now.AddDays(-5);
            var capture = new Capture(Guid.NewGuid(), "synthetic-old-reminder", capturedAt, "Asia/Shanghai", CaptureParseStatus.Pending, null, capturedAt);
            db.Captures.Add(capture);
            new LegacyMemoMigration(db.Factory, new Clock(Now), NullLogger<LegacyMemoMigration>.Instance).Run();
            var memo = db.Queries.QueryItems("capture_id = $id", ("$id", capture.Id.ToString("N"))).Single();
            var a = new MemoCaptureResult(capture, memo, false);
            db.Apply.Apply(db.Result(a, new[] { Candidate("old", capturedAt.AddHours(1)) }).Id, Now);
            return (db.Reminders.GetActiveForItem(memo.Id) is null, new { ActivePlans = db.Count("reminders"), Target = db.Reminders.GetActiveForItem(memo.Id)?.ReminderAtUtc });
        });
        Check("SS01", "main memo search includes original text after AI rewrite", () =>
        {
            var db = new Db(); var a = db.Capture("synthetic-raw-only-keyword");
            db.Apply.Apply(db.Result(a, new[] { Candidate("rewritten-summary") }).Id, Now);
            var vm = db.Memos(new HotkeyStatus()); vm.SearchText = "raw-only-keyword"; vm.Refresh();
            return (vm.Rows.Count == 1, new { MainSearchResults = vm.Rows.Count });
        });
    }

    private static void RunUi()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var xml = XDocument.Load(Path.Combine(Root, "src", "MemoDesk.App", "App.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(ns + "ResourceDictionary", xml.Root!.Attributes().Where(a => a.IsNamespaceDeclaration).Select(a => new XAttribute(a)), xml.Root.Element(ns + "Application.Resources")!.Elements().Select(e => new XElement(e)));
        var resourceXaml = dictionary.ToString()
            .Replace("clr-namespace:MemoDesk.App.Converters", "clr-namespace:MemoDesk.App.Converters;assembly=MemoDesk.App", StringComparison.Ordinal)
            .Replace("clr-namespace:MemoDesk.App.ViewModels", "clr-namespace:MemoDesk.App.ViewModels;assembly=MemoDesk.App", StringComparison.Ordinal)
            .Replace("clr-namespace:MemoDesk.App.Views", "clr-namespace:MemoDesk.App.Views;assembly=MemoDesk.App", StringComparison.Ordinal);
        app.Resources = (ResourceDictionary)XamlReader.Parse(resourceXaml);
        var db = new Db(useRealClock: true); var status = new HotkeyStatus(); db.Settings.Hotkey = "Ctrl+Alt+Shift+F21";
        using var hotkeys = new GlobalHotkeyService(); var manager = new HotkeyManager(hotkeys, db.Settings, status); manager.Start();
        db.Capture("隔离验收：核对日期日历和快捷键显示");
        var memoVm = db.Memos(status);
        var workVm = new WorkLogsViewModel(db.Queries, db.ItemService, db.Settings, db.Dialogs, db.Captures, db.Clock);
        var settingsVm = new SettingsViewModel(db.Settings, manager);
        var capture = new QuickCaptureWindow(db.CaptureService, new WindowForegroundTracker(), new MonitorLocator(), db.Settings, status);
        var memoView = new MemosView { DataContext = memoVm };
        var workView = new WorkLogsView { DataContext = workVm };
        var settingsView = new SettingsView { DataContext = settingsVm };
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "备忘", Content = memoView });
        tabs.Items.Add(new TabItem { Header = "工作记录", Content = workView });
        tabs.Items.Add(new TabItem { Header = "设置", Content = settingsView });
        tabs.SelectionChanged += (_, _) => { if (tabs.SelectedIndex == 0) memoVm.Refresh(); if (tabs.SelectedIndex == 1) workVm.Refresh(); };
        memoVm.QuickCaptureRequested += capture.ShowForCapture; manager.CaptureRequested += capture.ShowForCapture;
        capture.Captured += _ => { memoVm.Refresh(); workVm.Refresh(); };
        var window = new Window { Title = "[隔离验收] 备忘助手", Width = 1100, Height = 820, Content = tabs, FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"), Background = (System.Windows.Media.Brush)app.Resources["PaperBrush"] };
        AddUiCases(tabs, db, window);
        app.MainWindow = window;
        File.WriteAllText(Path.Combine(Output, "ui-session.json"), JsonSerializer.Serialize(new { db.Factory.DatabasePath, ProductionViews = true, ProductionApplicationLifecycle = false, NetworkCalls = false, NativeNotifications = false, Key = db.Settings.Hotkey }));
        memoVm.Refresh(); workVm.Refresh(); app.Run(window);
    }

    private static MemoParseCandidate Candidate(
        string content,
        DateTimeOffset? remind = null,
        string? sourceText = null,
        string? remindText = null) => new()
    {
        Content = content,
        RemindAtUtc = remind,
        SourceText = sourceText,
        RemindText = remindText,
    };
    private static void Check(string id, string expected, Func<(bool Passed, object Actual)> action)
    {
        try { var (passed, actual) = action(); Results.Add(new { Id = id, Status = passed ? "PASS" : "FAIL", Expected = expected, Actual = actual }); }
        catch (Exception ex) { Results.Add(new { Id = id, Status = "ERROR", Expected = expected, Actual = ex.ToString() }); }
    }
    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) if (File.Exists(Path.Combine(dir.FullName, "MemoDesk.sln"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found");
    }
    private sealed class Clock : TimeProvider { private readonly DateTimeOffset _now; public Clock(DateTimeOffset now) { _now = now; } public override DateTimeOffset GetUtcNow() => _now; }
    private sealed class Secret : ISecretProtector { public string Protect(string value) => value; public string Unprotect(string value) => value; }
    private sealed class Parser : IMemoAiParser { public Task<MemoParseResponse> ParseMemoAsync(AiParseRequest request, CancellationToken token) => throw new InvalidOperationException("No network in acceptance probe"); }
    private sealed class Db
    {
        public readonly SqliteConnectionFactory Factory;
        public readonly SqliteCaptureRepository Captures;
        public readonly SqliteItemStore Items;
        public readonly SqliteReminderRepository Reminders;
        public readonly SqliteMemoParseRepository ResultsStore;
        public readonly SqliteMemoApplyStore Apply;
        public readonly ItemQueryService Queries;
        public readonly MemoCaptureService CaptureService;
        public readonly ItemService ItemService;
        public readonly AppSettings Settings;
        public readonly DialogService Dialogs;
        public readonly TimeProvider Clock;
        public Db(bool useRealClock = false)
        {
            var path = Path.Combine(Output, "synthetic-data", Guid.NewGuid().ToString("N"), "memo.db");
            Factory = new SqliteConnectionFactory(path); new MigrationRunner(Factory).Migrate();
            Clock = useRealClock ? TimeProvider.System : new Clock(Now);
            Captures = new SqliteCaptureRepository(Factory); Items = new SqliteItemStore(Factory); Reminders = new SqliteReminderRepository(Factory);
            ResultsStore = new SqliteMemoParseRepository(Factory); Apply = new SqliteMemoApplyStore(Factory); Queries = new ItemQueryService(Factory);
            CaptureService = new MemoCaptureService(new SqliteMemoCaptureStore(Factory), Clock); ItemService = new ItemService(Items, Clock);
            Settings = new AppSettings(new SqliteSettingsStore(Factory), new Secret()); Dialogs = new DialogService(ItemService, Reminders, Clock);
        }
        public MemoCaptureResult Capture(string raw) => CaptureService.Capture(raw, "Asia/Shanghai");
        public MemoParseCoordinator Coordinator() => new(new Parser(), Captures, ResultsStore, Apply, Clock, NullLogger<MemoParseCoordinator>.Instance);
        public MemosViewModel Memos(HotkeyStatus status) => new(Queries, ItemService, Settings, Dialogs, Reminders, Clock, status, ResultsStore, Coordinator());
        public MemoParseRecord Result(MemoCaptureResult capture, IReadOnlyList<MemoParseCandidate> candidates)
        {
            var normalized = MemoCandidateNormalizer.Normalize(candidates, capture.Capture.Id, capture.Capture.ParseOperationVersion);
            var record = new MemoParseRecord(Guid.NewGuid(), capture.Capture.Id, capture.Capture.ParseOperationVersion, 1, MemoParseStatus.AwaitingApply, normalized, "synthetic-json", "synthetic", MemoParsePrompt.Version, MemoParsePrompt.SchemaVersion, 1, null, Now, Now);
            ResultsStore.Add(record); return record;
        }
        public long Count(string table) { using var connection = Factory.Create(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table; return Convert.ToInt64(command.ExecuteScalar()); }
        public void Sql(string sql) { using var connection = Factory.Create(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    }
}
