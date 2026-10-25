using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MemoDesk.App.Views;
using MemoDesk.Core.Items;

namespace ReviewProbe;

internal static partial class Program
{
    private static void AddUiCases(TabControl tabs, Db db, Window owner)
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "生产编辑窗口 + 隔离数据库。所有操作仅修改本轮验收数据。", Margin = new Thickness(0, 0, 0, 16) });
        void Button(string label, Action action)
        {
            var button = new Button { Content = label, Width = 260, Height = 38, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
            button.Click += (_, _) => action(); panel.Children.Add(button);
        }
        Button("日期保存验证", () =>
        {
            var a = db.Capture("日期保存验证：原有日期不能被无效输入静默保存");
            var changed = db.Dialogs.EditItem(a.Memo, db.Settings.TimeZone);
            LogUi(new { Case = "invalid-date-save", Changed = changed, Before = a.Memo, After = db.Items.GetById(a.Memo.Id) });
        });
        Button("秒级提醒编辑", () =>
        {
            var a = db.Capture("秒级提醒编辑：只改文字应保留原计划");
            var at = db.Clock.GetUtcNow().AddHours(1);
            var target = new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, at.Minute, 37, TimeSpan.Zero);
            db.Reminders.SetReminder(a.Memo.Id, target, db.Clock.GetUtcNow());
            var before = db.Reminders.GetActiveForItem(a.Memo.Id);
            var changed = db.Dialogs.EditItem(a.Memo, db.Settings.TimeZone);
            LogUi(new { Case = "second-precision-reminder", Changed = changed, Before = before, After = db.Reminders.GetActiveForItem(a.Memo.Id) });
        });
        Button("保存故障验证", () =>
        {
            var a = db.Capture("保存故障验证：修改内容后点保存，应保留窗口和输入");
            db.Sql("CREATE TRIGGER fail_ui_item_update BEFORE UPDATE ON items BEGIN SELECT RAISE(ABORT, 'synthetic UI save failure'); END;");
            try
            {
                var changed = db.Dialogs.EditItem(a.Memo, db.Settings.TimeZone);
                LogUi(new { Case = "save-failure", Changed = changed, Before = a.Memo, After = db.Items.GetById(a.Memo.Id) });
            }
            finally { db.Sql("DROP TRIGGER fail_ui_item_update;"); }
        });
        Button("版本冲突验证", () =>
        {
            var a = db.Capture("版本冲突验证：修改内容后点保存，应保留输入");
            var injected = false;
            var dialog = new ItemEditWindow(a.Memo, db.Settings.TimeZone, null, null, request =>
            {
                if (!injected) { db.Items.Update(a.Memo with { Content = "最新数据库内容" }); injected = true; }
                return db.ItemService.UpdateWithReminder(request.Item, request.Reminder);
            }) { Owner = owner };
            var changed = dialog.ShowDialog() == true;
            LogUi(new { Case = "version-conflict", Changed = changed, After = db.Items.GetById(a.Memo.Id) });
        });
        tabs.Items.Add(new TabItem { Header = "隔离验收场景", Content = panel });
    }

    private static void LogUi(object data) => File.AppendAllText(Path.Combine(Output, "ui-actions.jsonl"), JsonSerializer.Serialize(data) + Environment.NewLine);
}
