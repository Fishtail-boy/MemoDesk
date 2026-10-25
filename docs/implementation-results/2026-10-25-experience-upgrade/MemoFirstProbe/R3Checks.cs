using System;
using System.Linq;
using MemoDesk.Core.Parsing;
using MemoDesk.Core.Time;

namespace ReviewProbe;

internal static partial class Program
{
    private static void RunR3Checks()
    {
        Check("R3-01", "local negation must remain scoped when the same two items are separated with commas", () =>
        {
            var first = "明天下午三点提交报表"; var second = "明天下午四点开会";
            var raw = first + "，不提醒，" + second + "要提醒";
            var target = new DateTimeOffset(2026, 10, 26, 15, 0, 0, TimeSpan.FromHours(8));
            var candidates = new[] { Candidate("报表", target, first, "明天下午三点"), Candidate("开会", target.AddHours(1), second, "明天下午四点") };
            var db = new Db(); var a = db.Capture(raw);
            var decisions = MemoReminderRules.EvaluateAll(raw, candidates, Now, BusinessTime.Resolve("Asia/Shanghai"));
            db.Apply.Apply(db.Result(a, candidates).Id, Now);
            var plans = db.Queries.QueryItems("capture_id = $id", ("$id", a.Capture.Id.ToString("N")))
                .Select(item => new { item.Content, Plan = db.Reminders.GetActiveForItem(item.Id) })
                .Where(row => row.Plan is not null).ToArray();
            return (plans.Length == 1 && plans[0].Content == "开会", new { Raw = raw, decisions, Plans = plans.Select(row => new { row.Content, row.Plan!.ReminderAtUtc }).ToArray() });
        });
    }
}
