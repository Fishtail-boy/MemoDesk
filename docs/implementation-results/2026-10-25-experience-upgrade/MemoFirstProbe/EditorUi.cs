using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using System.Xml.Linq;
using MemoDesk.App.Views;
using MemoDesk.Core.Items;

namespace ReviewProbe;

internal static partial class Program
{
    private static void RunEditorUi(string[] args)
    {
        var mode = args.Contains("--seconds") ? "seconds" : args.Contains("--failure") ? "failure" : args.Contains("--conflict") ? "conflict" : "date";
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var xml = XDocument.Load(Path.Combine(Root, "src", "MemoDesk.App", "App.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(ns + "ResourceDictionary", xml.Root!.Attributes().Where(a => a.IsNamespaceDeclaration).Select(a => new XAttribute(a)), xml.Root.Element(ns + "Application.Resources")!.Elements().Select(e => new XElement(e)));
        var resource = dictionary.ToString()
            .Replace("clr-namespace:MemoDesk.App.Converters", "clr-namespace:MemoDesk.App.Converters;assembly=MemoDesk.App", StringComparison.Ordinal)
            .Replace("clr-namespace:MemoDesk.App.ViewModels", "clr-namespace:MemoDesk.App.ViewModels;assembly=MemoDesk.App", StringComparison.Ordinal)
            .Replace("clr-namespace:MemoDesk.App.Views", "clr-namespace:MemoDesk.App.Views;assembly=MemoDesk.App", StringComparison.Ordinal);
        app.Resources = (ResourceDictionary)XamlReader.Parse(resource);
        var db = new Db(useRealClock: true); var a = db.Capture("独立编辑验收：" + mode);
        if (mode == "seconds")
        {
            var at = db.Clock.GetUtcNow().AddHours(1);
            db.Reminders.SetReminder(a.Memo.Id, new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, at.Minute, 37, TimeSpan.Zero), db.Clock.GetUtcNow());
        }
        if (mode == "failure")
            db.Sql("CREATE TRIGGER fail_editor_update BEFORE UPDATE ON items BEGIN SELECT RAISE(ABORT, 'synthetic editor write failure'); END;");
        var beforePlan = db.Reminders.GetActiveForItem(a.Memo.Id);
        var injected = false; ItemEditSaveRequest? lastRequest = null; MemoEditSaveResult? lastResult = null;
        var editor = new ItemEditWindow(a.Memo, db.Settings.TimeZone, beforePlan?.ReminderAtUtc, beforePlan, request =>
        {
            lastRequest = request;
            if (mode == "conflict" && !injected) { db.Items.Update(a.Memo with { Content = "数据库并发修改后的新内容" }); injected = true; }
            lastResult = db.ItemService.UpdateWithReminder(request.Item, request.Reminder); return lastResult;
        }) { Title = "[隔离验收] 编辑-" + mode, ShowInTaskbar = true, WindowStyle = WindowStyle.SingleBorderWindow, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        app.MainWindow = editor;
        var changed = editor.ShowDialog() == true;
        LogUi(new { Case = "editor-" + mode, Changed = changed, Before = a.Memo, After = db.Items.GetById(a.Memo.Id), BeforePlan = beforePlan, AfterPlan = db.Reminders.GetActiveForItem(a.Memo.Id), LastRequest = lastRequest, LastResult = lastResult });
        app.Shutdown();
    }
}
