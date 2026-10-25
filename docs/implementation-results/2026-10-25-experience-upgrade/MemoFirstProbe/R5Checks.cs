using System;
using System.Linq;
using MemoDesk.Core.Parsing;
using MemoDesk.Core.Time;

namespace ReviewProbe;

internal static partial class Program
{
    private static void RunR5Checks()
    {
        Check("R5-01", "a comma-delimited suffix negation must stay with the preceding item when the next item explicitly requests a reminder", () =>
        {
            var first = "明天下午三点提交报表";
            var second = "明天下午四点开会";
            var raw = first + "，不要提醒，" + second + "要提醒";
            var target = new DateTimeOffset(2026, 10, 26, 15, 0, 0, TimeSpan.FromHours(8));
            var candidates = new[]
            {
                Candidate("报表", target, first, "明天下午三点"),
                Candidate("开会", target.AddHours(1), second, "明天下午四点")
            };
            var db = new Db(); var captured = db.Capture(raw);
            var decisions = MemoReminderRules.EvaluateAll(raw, candidates, Now, BusinessTime.Resolve("Asia/Shanghai"));
            db.Apply.Apply(db.Result(captured, candidates).Id, Now);
            var plans = db.Queries.QueryItems("capture_id = $id", ("$id", captured.Capture.Id.ToString("N")))
                .Select(item => new { item.Content, Plan = db.Reminders.GetActiveForItem(item.Id) })
                .Where(row => row.Plan is not null).ToArray();
            return (plans.Length == 1 && plans[0].Content == "开会", new
            {
                Raw = raw,
                Sources = candidates.Select(c => c.SourceText).ToArray(),
                ExpectedContent = "开会",
                decisions,
                Plans = plans.Select(row => new { row.Content, row.Plan!.ReminderAtUtc }).ToArray()
            });
        });
    }
}
