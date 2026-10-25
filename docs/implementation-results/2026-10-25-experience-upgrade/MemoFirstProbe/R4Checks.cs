using System;
using System.Linq;
using MemoDesk.Core.Parsing;
using MemoDesk.Core.Time;

namespace ReviewProbe;

internal static partial class Program
{
    private static void RunR4Checks()
    {
        foreach (var includeNegationInSource in new[] { false, true })
        {
            Check(includeNegationInSource ? "R4-02" : "R4-01", "a prefix negation on the second item must reject only that item even when omitted from its model source", () =>
            {
                var first = "明天下午三点提交报表";
                var second = "明天下午四点开会";
                var raw = first + "要提醒，不要提醒" + second;
                var target = new DateTimeOffset(2026, 10, 26, 15, 0, 0, TimeSpan.FromHours(8));
                var candidates = new[]
                {
                    Candidate("报表", target, first, "明天下午三点"),
                    Candidate("开会", target.AddHours(1), includeNegationInSource ? "不要提醒" + second : second, "明天下午四点")
                };
                var db = new Db(); var captured = db.Capture(raw);
                var decisions = MemoReminderRules.EvaluateAll(raw, candidates, Now, BusinessTime.Resolve("Asia/Shanghai"));
                db.Apply.Apply(db.Result(captured, candidates).Id, Now);
                var plans = db.Queries.QueryItems("capture_id = $id", ("$id", captured.Capture.Id.ToString("N")))
                    .Select(item => new { item.Content, Plan = db.Reminders.GetActiveForItem(item.Id) })
                    .Where(row => row.Plan is not null).ToArray();
                return (plans.Length == 1 && plans[0].Content == "报表", new
                {
                    Raw = raw,
                    IncludeNegationInSource = includeNegationInSource,
                    Sources = candidates.Select(c => c.SourceText).ToArray(),
                    ExpectedContent = "报表",
                    decisions,
                    Plans = plans.Select(row => new { row.Content, row.Plan!.ReminderAtUtc }).ToArray()
                });
            });
        }
    }
}
