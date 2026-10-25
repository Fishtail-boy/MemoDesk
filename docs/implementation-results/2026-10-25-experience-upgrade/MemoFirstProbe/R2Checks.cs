using System;
using System.Linq;
using MemoDesk.Core.Parsing;
using MemoDesk.Core.Time;

namespace ReviewProbe;

internal static partial class Program
{
    private static void RunR2Checks()
    {
        var monday = new DateTimeOffset(2026, 10, 26, 15, 0, 0, TimeSpan.FromHours(8));
        foreach (var (id, raw, target, expectedAllow) in new[]
        {
            ("R2-01", "下周一十五点提醒我交报表", monday, true),
            ("R2-02", "明天下午三点一刻提醒我交报表", monday.AddMinutes(15), true),
            ("R2-03", "明天下午三点一刻钟提醒我交报表", monday.AddMinutes(15), true),
            ("R2-04", "明天下午三点三刻提醒我交报表", monday.AddMinutes(3), false),
            ("R2-05", "下周二 十五点提醒我交报表", monday.AddDays(1), true),
            ("R2-06", "下周五下午三点提醒我交报表", monday.AddDays(4), true)
        })
        {
            Check(id, "complete weekday/minute expressions must produce the intended target or reject an invalid model target", () =>
            {
                var db = new Db(); var a = db.Capture(raw);
                var candidate = Candidate(raw, target, raw, raw);
                var decisions = MemoReminderRules.EvaluateAll(raw, new[] { candidate }, Now, BusinessTime.Resolve("Asia/Shanghai"));
                db.Apply.Apply(db.Result(a, new[] { candidate }).Id, Now);
                var plan = db.Reminders.GetActiveForItem(a.Memo.Id);
                return ((plan is not null) == expectedAllow, new { Raw = raw, ExpectedAllow = expectedAllow, decisions[0].Allow, decisions[0].Reason, Target = plan?.ReminderAtUtc });
            });
        }
        foreach (var denyFirst in new[] { true, false })
        {
            Check(denyFirst ? "R2-07" : "R2-08", "a truncated local negation must affect only its own candidate in either order", () =>
            {
                var first = "明天下午三点提交报表"; var second = "明天下午四点开会";
                var raw = denyFirst ? first + "，不提醒；" + second + "要提醒" : first + "要提醒；" + second + "，不提醒";
                var db = new Db(); var a = db.Capture(raw);
                var candidates = new[] { Candidate("报表", monday, first, "明天下午三点"), Candidate("开会", monday.AddHours(1), second, "明天下午四点") };
                var decisions = MemoReminderRules.EvaluateAll(raw, candidates, Now, BusinessTime.Resolve("Asia/Shanghai"));
                db.Apply.Apply(db.Result(a, candidates).Id, Now);
                var items = db.Queries.QueryItems("capture_id = $id", ("$id", a.Capture.Id.ToString("N")));
                var plans = items.Select(item => new { item.Content, Plan = db.Reminders.GetActiveForItem(item.Id) }).Where(row => row.Plan is not null).ToArray();
                var expected = denyFirst ? "开会" : "报表";
                return (plans.Length == 1 && plans[0].Content == expected, new { Raw = raw, ExpectedContent = expected, decisions, Plans = plans.Select(row => new { row.Content, row.Plan!.ReminderAtUtc }).ToArray() });
            });
        }
        Check("R2-09", "partially overlapping evidence spans must be rejected even if neither string contains the other", () =>
        {
            var raw = "明天十五点开会，后天十六点交表"; var db = new Db(); var a = db.Capture(raw);
            var candidates = new[]
            {
                Candidate("开会", monday.AddDays(1), "明天十五点开会，后天", "明天十五点"),
                Candidate("交表", monday.AddDays(1).AddHours(1), "后天十六点交表", "后天十六点")
            };
            var decisions = MemoReminderRules.EvaluateAll(raw, candidates, Now, BusinessTime.Resolve("Asia/Shanghai"));
            db.Apply.Apply(db.Result(a, candidates).Id, Now);
            return (db.Count("reminders") == 0, new { Raw = raw, Ambiguous = MemoReminderRules.HasAmbiguousSources(candidates), decisions, Plans = db.Count("reminders") });
        });
    }
}
