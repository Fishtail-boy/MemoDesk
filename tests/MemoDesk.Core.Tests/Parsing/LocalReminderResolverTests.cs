using MemoDesk.Core.Parsing;
using MemoDesk.Core.Time;

namespace MemoDesk.Core.Tests.Parsing;

public sealed class LocalReminderResolverTests
{
    private static readonly DateTimeOffset Captured = new(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Zone = BusinessTime.Resolve("Asia/Shanghai");

    [Theory]
    [InlineData("3s后提醒我尿尿", 3)]
    [InlineData("3S 后提醒我尿尿", 3)]
    [InlineData("三秒后提醒我尿尿", 3)]
    [InlineData("30秒后提醒我喝水", 30)]
    [InlineData("1分30秒后提醒我检查结果", 90)]
    [InlineData("1m30s后提醒我检查结果", 90)]
    public void ExplicitRelativeCommand_IsResolvedWithoutAi(string raw, int seconds)
    {
        var result = LocalReminderResolver.Resolve(raw, Captured, Zone);
        Assert.NotNull(result);
        Assert.Equal(Captured.AddSeconds(seconds), result.TargetAtUtc);
        Assert.Equal(seconds, result.RelativeSeconds);
    }

    [Theory]
    [InlineData("3s后不提醒我尿尿")]
    [InlineData("接口耗时3s")]
    [InlineData("刚才等了3秒")]
    [InlineData("-3s后提醒我尿尿")]
    [InlineData("3ms后提醒我尿尿")]
    [InlineData("3s后提醒我尿尿，10s后提醒我喝水")]
    [InlineData("1年3秒后提醒我检查结果")]
    [InlineData("999999999999999999999s后提醒我喝水")]
    public void UnclearOrInvalidInput_OnlySavesMemo(string raw) =>
        Assert.Null(LocalReminderResolver.Resolve(raw, Captured, Zone));

    [Fact]
    public void SecondsEvidence_RejectsModelTargetOffByThirtySeconds()
    {
        var candidate = new MemoParseCandidate { Content="尿尿", SourceText="3s后提醒我尿尿",
            RemindText="3s后", RemindAtUtc=Captured.AddSeconds(33) };
        Assert.False(MemoReminderRules.Evaluate(candidate.SourceText, candidate, Captured, Zone).Allow);
    }
}
