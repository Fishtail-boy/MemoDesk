using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using MemoDesk.Core.Capturing;
using MemoDesk.Core.Items;
using MemoDesk.Core.Parsing;
using MemoDesk.Core.Time;
using MemoDesk.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReviewProbe;

internal static partial class Program
{
    private static void RunExtraChecks()
    {
        var future = new DateTimeOffset(2026, 10, 26, 15, 0, 0, TimeSpan.FromHours(8));
        foreach (var (id, raw, source, time, target, allow) in new[]
        {
            ("EX01", "明天下午三点提交报表", "明天下午三点提交报表", "明天下午三点", future, true),
            ("EX02", "明天下午三点提交报表", "明天下午三点提交报表", "明天下午三点", future.AddHours(1), false),
            ("EX03", "明天下午三点提交报表，不提醒", "明天下午三点提交报表，不提醒", "明天下午三点", future, false),
            ("EX04", "明天下午三点提交报表，不提醒", "明天下午三点提交报表", "明天下午三点", future, false),
            ("EX05", "下周一十五点提醒我交报表", "下周一十五点提醒我交报表", "下周一十五点", future.AddDays(-1), false),
            ("EX06", "明天下午处理报表", "明天下午处理报表", "明天下午", future, false),
            ("EX07", "明天下午三点一刻提醒我交报表", "明天下午三点一刻提醒我交报表", "明天下午三点一刻", future, false)
        })
        {
            Check(id, "per-candidate evidence and original intent must determine the exact reminder", () =>
            {
                var db = new Db(); var capture = db.Capture(raw);
                var candidate = Candidate(raw, target, source, time);
                var decision = MemoReminderRules.Evaluate(raw, candidate, Now, BusinessTime.Resolve("Asia/Shanghai"));
                db.Apply.Apply(db.Result(capture, new[] { candidate }).Id, Now);
                var plan = db.Reminders.GetActiveForItem(capture.Memo.Id);
                return ((plan is not null) == allow, new { Raw = raw, Source = source, decision.Allow, decision.Reason, Target = plan?.ReminderAtUtc, ExpectedAllowed = allow });
            });
        }

        Check("EX08", "missing migration origin cannot enable overdue catch-up", () =>
        {
            var db = new Db(); const string raw = "2026-10-25 11:00 提醒我交报表"; var a = db.Capture(raw);
            db.Sql("DELETE FROM migration_context;");
            db.Apply.Apply(db.Result(a, new[] { Candidate("报表", Now.AddHours(1), raw, "2026-10-25 11:00") }).Id, Now.AddHours(2));
            return (db.Count("reminders") == 0, new { Plans = db.Count("reminders") });
        });
        Check("EX09", "a new delayed future reminder retains its original target and applies once", () =>
        {
            var db = new Db(); const string raw = "2026-10-25 11:00 提醒我交报表"; var a = db.Capture(raw);
            var result = db.Result(a, new[] { Candidate("报表", Now.AddHours(1), raw, "2026-10-25 11:00") });
            db.Apply.Apply(result.Id, Now.AddHours(2)); db.Apply.Apply(result.Id, Now.AddHours(3));
            var plan = db.Reminders.GetActiveForItem(a.Memo.Id);
            return (db.Count("reminders") == 1 && plan?.ReminderAtUtc == Now.AddHours(1), new { Plans = db.Count("reminders"), Target = plan?.ReminderAtUtc });
        });
        foreach (var isFuture in new[] { false, true })
        {
            Check(isFuture ? "EX11" : "EX10", "migrated origin suppresses historical targets while retaining valid future targets", () =>
            {
                var db = new Db(); var captured = Now.AddDays(-5); var target = isFuture ? Now.AddDays(1).AddHours(1) : captured.AddHours(1);
                var text = TimeZoneInfo.ConvertTime(target, BusinessTime.Resolve("Asia/Shanghai")).ToString("yyyy-MM-dd HH:mm");
                var raw = text + " 提醒我交旧报表";
                var old = new Capture(Guid.NewGuid(), raw, captured, "Asia/Shanghai", CaptureParseStatus.Pending, null, captured);
                db.Captures.Add(old); new LegacyMemoMigration(db.Factory, new Clock(Now), NullLogger<LegacyMemoMigration>.Instance).Run();
                var memo = db.Queries.QueryItems("capture_id = $id", ("$id", old.Id.ToString("N"))).Single();
                var a = new MemoCaptureResult(old, memo, false);
                db.Apply.Apply(db.Result(a, new[] { Candidate("旧报表", target, raw, text) }).Id, Now);
                var plan = db.Reminders.GetActiveForItem(memo.Id);
                return ((plan is not null) == isFuture, new { isFuture, Plan = plan?.ReminderAtUtc });
            });
        }
        Check("EX12", "persisted legacy results without evidence are applied without API or unsafe reminders", () =>
        {
            var db = new Db(); var a = db.Capture("记住没有时间的信息");
            var result = db.Result(a, new[] { Candidate("legacy summary", Now.AddHours(1)) });
            db.Sql("UPDATE memo_parse_results SET schema_version = 'old-schema';");
            db.Coordinator().ProcessAsync(a.Capture.Id, CancellationToken.None).GetAwaiter().GetResult();
            return (db.Count("reminders") == 0 && db.ResultsStore.GetById(result.Id)?.Status == MemoParseStatus.Applied,
                new { Plans = db.Count("reminders"), Status = db.ResultsStore.GetById(result.Id)?.Status });
        });
        Check("EX13", "editing only content retains the latest reminder despite a newer plan version", () =>
        {
            var db = new Db(); var a = db.Capture("synthetic-content"); db.Reminders.SetReminder(a.Memo.Id, Now.AddHours(1), Now);
            var old = db.Reminders.GetActiveForItem(a.Memo.Id)!; db.Reminders.SetReminder(a.Memo.Id, Now.AddHours(2), Now);
            var latest = db.Reminders.GetActiveForItem(a.Memo.Id)!;
            var edit = db.Items.UpdateWithReminder(a.Memo with { Content = "edited-content" }, new ReminderEditIntent(ReminderEditKind.Unchanged, old.ReminderAtUtc, old.Id, old.PlanVersion, true), Now);
            var after = db.Reminders.GetActiveForItem(a.Memo.Id)!;
            return (edit.IsSuccess && after.PlanVersion == latest.PlanVersion && after.ReminderAtUtc == latest.ReminderAtUtc,
                new { edit.Outcome, after.PlanVersion, after.ReminderAtUtc });
        });
        Check("EX14", "changing an outdated reminder snapshot rolls back content and keeps the latest plan", () =>
        {
            var db = new Db(); var a = db.Capture("before-conflict"); db.Reminders.SetReminder(a.Memo.Id, Now.AddHours(1), Now);
            var old = db.Reminders.GetActiveForItem(a.Memo.Id)!; db.Reminders.SetReminder(a.Memo.Id, Now.AddHours(2), Now);
            var result = db.Items.UpdateWithReminder(a.Memo with { Content = "must-not-commit" }, new ReminderEditIntent(ReminderEditKind.Set, Now.AddHours(3), old.Id, old.PlanVersion, true), Now);
            return (result.Outcome == MemoEditOutcome.ReminderVersionConflict && db.Items.GetById(a.Memo.Id)!.Content == "before-conflict" && db.Reminders.GetActiveForItem(a.Memo.Id)!.ReminderAtUtc == Now.AddHours(2),
                new { result.Outcome, Content = db.Items.GetById(a.Memo.Id)!.Content });
        });
        foreach (var parsing in new[] { true, false })
        {
            Check(parsing ? "EX15" : "EX16", "recovery scans all 1001 rows including tied timestamps and deduplicates captures", () =>
            {
                var db = new Db();
                using (var connection = db.Factory.Create()) using (var transaction = connection.BeginTransaction())
                {
                    for (var i = 0; i < 1001; i++)
                    {
                        var id = Guid.NewGuid().ToString("N");
                        using var c = connection.CreateCommand(); c.Transaction = transaction;
                        c.CommandText = "INSERT INTO captures(id,raw_text,captured_at_utc,captured_time_zone_id,parse_status,recorded_at_utc,parse_operation_version) VALUES($id,'synthetic','2026-10-25T02:00:00.0000000+00:00','Asia/Shanghai',$status,'2026-10-25T02:00:00.0000000+00:00',1)";
                        c.Parameters.AddWithValue("$id", id); c.Parameters.AddWithValue("$status", parsing ? "Parsing" : "Pending"); c.ExecuteNonQuery();
                        if (!parsing)
                        {
                            using var r = connection.CreateCommand(); r.Transaction = transaction;
                            r.CommandText = "INSERT INTO memo_parse_results(id,capture_id,operation_version,version,status,candidates_json,attempt_count,created_at_utc,updated_at_utc) VALUES($id,$capture,1,1,'AwaitingApply','[]',1,'2026-10-25T02:00:00.0000000+00:00','2026-10-25T02:00:00.0000000+00:00')";
                            r.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); r.Parameters.AddWithValue("$capture", id); r.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                }
                var coordinator = db.Coordinator(); typeof(MemoParseCoordinator).GetMethod("Recover", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(coordinator, null);
                var queue = (Channel<Guid>)typeof(MemoParseCoordinator).GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(coordinator)!;
                return (queue.Reader.Count == 1001 && db.Captures.GetByStatus(CaptureParseStatus.Parsing, 1).Count == 0, new { parsing, Queued = queue.Reader.Count });
            });
        }
        foreach (var term in new[] { "RAW-ONLY", "中文标签", "%_\\" })
        {
            Check("EX17-" + term, "unified search includes raw text, decoded tags and literal wildcard characters", () =>
            {
                var db = new Db(); var a = db.Capture("raw-only 中文原文 %_\\");
                var candidate = Candidate("summary"); candidate.Tags.Add("中文标签");
                db.Apply.Apply(db.Result(a, new[] { candidate }).Id, Now);
                var vm = db.Memos(new MemoDesk.App.Services.HotkeyStatus()); vm.SearchText = term; vm.Refresh();
                return (vm.Rows.Count == 1, new { term, Results = vm.Rows.Count });
            });
        }
    }
}
