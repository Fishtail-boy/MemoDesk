using MemoDesk.Core.Domain;
using MemoDesk.Core.Parsing;
using MemoDesk.Core.Time;
using Xunit;

namespace MemoDesk.Core.Tests.Parsing;

public sealed class MemoReminderRulesTests
{
    private static readonly DateTimeOffset Captured = new(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Zone = BusinessTime.Resolve("Asia/Shanghai");

    [Fact]
    public void ExplicitFutureTime_MustMatchCapturedTimezoneAndText()
    {
        var target = BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(15, 0), Zone);
        var candidate = Candidate("明天下午三点提交报表", "明天下午三点", target);

        var decision = MemoReminderRules.Evaluate("明天下午三点提交报表", candidate, Captured, Zone);

        Assert.True(decision.Allow);
        Assert.Equal(target, decision.TargetAtUtc);
        Assert.Equal(MemoReminderIntent.ImplicitFutureEvent, decision.Intent);
        Assert.Equal(MemoReminderPrecision.Exact, decision.Precision);
    }

    [Fact]
    public void NegationAfterTruncatedSource_StillAppliesWithinTheSameItem()
    {
        var target = BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(15, 0), Zone);
        var candidate = Candidate("明天下午三点提交报表", "明天下午三点", target);

        var decision = Assert.Single(MemoReminderRules.EvaluateAll(
            "明天下午三点提交报表，不提醒",
            new[] { candidate },
            Captured,
            Zone));

        Assert.False(decision.Allow);
        Assert.Equal(MemoReminderIntent.Forbidden, decision.Intent);
    }

    [Fact]
    public void LocalNegation_DoesNotLeakToAnotherItem()
    {
        var first = Candidate("明天下午三点提交报表", "明天下午三点", BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(15, 0), Zone));
        var second = Candidate("后天上午十点联系张工", "后天上午十点", BusinessTime.Combine(new DateOnly(2026, 10, 27), new TimeOnly(10, 0), Zone));

        var decisions = MemoReminderRules.EvaluateAll(
            "明天下午三点提交报表，后天上午十点联系张工，不提醒联系张工",
            new[] { first, second },
            Captured,
            Zone);

        Assert.True(decisions[0].Allow);
        Assert.False(decisions[1].Allow);
    }

    [Fact]
    public void SemicolonSeparatedNegation_DoesNotLeakForward()
    {
        var first = Candidate("明天下午三点提交报表", "明天下午三点", BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(15, 0), Zone));
        var second = Candidate("明天下午四点开会", "明天下午四点", BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(16, 0), Zone));

        var decisions = MemoReminderRules.EvaluateAll(
            "明天下午三点提交报表，不提醒；明天下午四点开会要提醒",
            new[] { first, second },
            Captured,
            Zone);

        Assert.False(decisions[0].Allow);
        Assert.True(decisions[1].Allow);
    }

    [Fact]
    public void CommaSeparatedNegation_DoesNotLeakForward()
    {
        var first = Candidate("明天下午三点提交报表", "明天下午三点", BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(15, 0), Zone));
        var second = Candidate("明天下午四点开会", "明天下午四点", BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(16, 0), Zone));

        var decisions = MemoReminderRules.EvaluateAll(
            "明天下午三点提交报表，不提醒，明天下午四点开会要提醒",
            new[] { first, second },
            Captured,
            Zone);

        Assert.False(decisions[0].Allow);
        Assert.True(decisions[1].Allow);
    }

    [Fact]
    public void CommaAfterPrefixNegation_KeepsItWithThePreviousItem()
    {
        var first = Candidate("明天下午三点提交报表", "明天下午三点", BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(15, 0), Zone));
        var second = Candidate("明天下午四点开会", "明天下午四点", BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(16, 0), Zone));

        var decisions = MemoReminderRules.EvaluateAll(
            "明天下午三点提交报表，不要提醒，明天下午四点开会要提醒",
            new[] { first, second },
            Captured,
            Zone);

        Assert.False(decisions[0].Allow);
        Assert.True(decisions[1].Allow);
    }

    [Fact]
    public void PrefixNegation_AttachesToTheFollowingItem()
    {
        var first = Candidate("明天下午三点提交报表", "明天下午三点", BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(15, 0), Zone));
        var second = Candidate("明天下午四点开会", "明天下午四点", BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(16, 0), Zone));

        var decisions = MemoReminderRules.EvaluateAll(
            "明天下午三点提交报表要提醒，不要提醒明天下午四点开会",
            new[] { first, second },
            Captured,
            Zone);

        Assert.True(decisions[0].Allow);
        Assert.False(decisions[1].Allow);
    }

    [Fact]
    public void PartiallyOverlappingSourceSpans_AreRejected()
    {
        var first = Candidate("开会", "明天十五点", BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(15, 0), Zone));
        first.SourceText = "明天十五点开会，后天";
        var second = Candidate("交表", "后天十六点", BusinessTime.Combine(new DateOnly(2026, 10, 27), new TimeOnly(16, 0), Zone));
        second.SourceText = "后天十六点交表";

        var decisions = MemoReminderRules.EvaluateAll(
            "明天十五点开会，后天十六点交表",
            new[] { first, second },
            Captured,
            Zone);

        Assert.All(decisions, decision => Assert.False(decision.Allow));
        Assert.True(MemoReminderRules.HasAmbiguousSources(
            "明天十五点开会，后天十六点交表",
            new[] { first, second }));
    }

    [Fact]
    public void WeekdayAndQuarterExpressions_AreComputedOrRejected()
    {
        var nextMonday = BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(15, 0), Zone);
        var weekday = Candidate("下周一十五点提醒我交报表", "下周一十五点", nextMonday);
        Assert.True(MemoReminderRules.Evaluate("下周一十五点提醒我交报表", weekday, Captured, Zone).Allow);

        var spaced = Candidate(
            "下周二 十五点提醒我交报表",
            "下周二 十五点",
            BusinessTime.Combine(new DateOnly(2026, 10, 27), new TimeOnly(15, 0), Zone));
        Assert.True(MemoReminderRules.Evaluate("下周二 十五点提醒我交报表", spaced, Captured, Zone).Allow);

        var wrongDay = Candidate("下周一十五点提醒我交报表", "下周一十五点", BusinessTime.Combine(new DateOnly(2026, 10, 25), new TimeOnly(15, 0), Zone));
        Assert.False(MemoReminderRules.Evaluate("下周一十五点提醒我交报表", wrongDay, Captured, Zone).Allow);

        var quarter = Candidate("明天下午三点一刻提醒我交报表", "明天下午三点一刻", BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(15, 15), Zone));
        Assert.True(MemoReminderRules.Evaluate("明天下午三点一刻提醒我交报表", quarter, Captured, Zone).Allow);

        var threeQuarter = Candidate("明天下午三点三刻提醒我交报表", "明天下午三点三刻", BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(15, 3), Zone));
        Assert.False(MemoReminderRules.Evaluate("明天下午三点三刻提醒我交报表", threeQuarter, Captured, Zone).Allow);
    }

    [Fact]
    public void WrongModelTarget_IsRejected()
    {
        var candidate = Candidate(
            "明天下午三点提交报表",
            "明天下午三点",
            BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(16, 0), Zone));

        var decision = MemoReminderRules.Evaluate("明天下午三点提交报表", candidate, Captured, Zone);

        Assert.False(decision.Allow);
        Assert.Contains("不一致", decision.Reason);
    }

    [Fact]
    public void DateOnlyOrFuzzyTime_IsRejected()
    {
        foreach (var (raw, source, remind) in new[]
        {
            ("明天下午处理报表", "明天下午处理报表", "明天下午"),
            ("明天处理报表", "明天处理报表", "明天"),
            ("记住接口字段名称", "记住接口字段名称", ""),
        })
        {
            var candidate = Candidate(source, remind, Captured.AddDays(1));
            var decision = MemoReminderRules.Evaluate(raw, candidate, Captured, Zone);
            Assert.False(decision.Allow);
        }
    }

    [Fact]
    public void ExplicitNoReminder_WinsOverModelTime()
    {
        var candidate = Candidate(
            "明天下午三点处理报表，不提醒",
            "明天下午三点处理报表，不提醒",
            BusinessTime.Combine(new DateOnly(2026, 10, 26), new TimeOnly(15, 0), Zone));

        var decision = MemoReminderRules.Evaluate(candidate.SourceText!, candidate, Captured, Zone);

        Assert.False(decision.Allow);
        Assert.Equal(MemoReminderIntent.Forbidden, decision.Intent);
    }

    [Fact]
    public void ImmediateIntent_TargetsCaptureTimeAndRejectsWrongModelTime()
    {
        var good = Candidate("现在提醒我提交报表", "现在提醒", Captured);
        var allowed = MemoReminderRules.Evaluate("现在提醒我提交报表", good, Captured, Zone);
        Assert.True(allowed.Allow);
        Assert.True(allowed.Immediate);
        Assert.Equal(Captured, allowed.TargetAtUtc);

        var wrong = Candidate("现在提醒我提交报表", "现在提醒", Captured.AddHours(1));
        Assert.False(MemoReminderRules.Evaluate("现在提醒我提交报表", wrong, Captured, Zone).Allow);
    }

    [Fact]
    public void AlreadyHappenedWork_IsRejectedEvenWithFutureModelTime()
    {
        var candidate = Candidate("昨天完成联调", "昨天完成联调", Captured.AddHours(1));
        candidate.WorkTime = new WorkTimeInfo(
            new DateOnly(2026, 10, 24), null, null, null, WorkTimePrecision.Date);

        var decision = MemoReminderRules.Evaluate("昨天完成联调", candidate, Captured, Zone);

        Assert.False(decision.Allow);
    }

    [Fact]
    public void MissingEvidence_IsRejectedForOldOrUntrustedResults()
    {
        var candidate = new MemoParseCandidate
        {
            Content = "旧结果",
            RemindAtUtc = Captured.AddHours(1),
        };

        var decision = MemoReminderRules.Evaluate("旧结果", candidate, Captured, Zone);

        Assert.False(decision.Allow);
    }

    private static MemoParseCandidate Candidate(
        string source,
        string remind,
        DateTimeOffset? target) => new()
    {
        Content = source,
        SourceText = source,
        RemindText = remind,
        RemindAtUtc = target,
    };
}
