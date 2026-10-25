using MemoDesk.Core;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Parsing;

namespace MemoDesk.Core.Tests.Parsing;

public sealed class MemoResponseMapperTests
{
    private static readonly DateTimeOffset Captured = new(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Map_DoesNotUseTypeAndKeepsExplicitFutureReminder()
    {
        var request = Request("明天下午三点提交报表");
        var json = """
            {
              "items": [
                {
                  "content": "提交报表",
                  "sourceText": "明天下午三点提交报表",
                  "remindText": "明天下午三点",
                  "remindAt": "2026-10-26T15:00:00+08:00",
                  "priority": "high",
                  "tags": ["报表"],
                  "issues": []
                }
              ],
              "notes": []
            }
            """;

        var result = MemoResponseMapper.Map(json, "test", request);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("提交报表", candidate.Content);
        Assert.Equal(ItemPriority.High, candidate.Priority);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 7, 0, 0, TimeSpan.Zero), candidate.RemindAtUtc);
        Assert.Contains("报表", candidate.Tags);
    }

    [Fact]
    public void Map_PastAtCaptureReminderIsDroppedAndWorkTimeIsPreserved()
    {
        var request = Request("昨天完成联调，明天三点提交报表");
        var json = """
            {
              "items": [
                {
                  "content": "完成联调",
                  "sourceText": "昨天完成联调",
                  "remindText": "昨天",
                  "remindAt": "2026-10-24T10:00:00+08:00",
                  "workTime": {"date": "2026-10-24", "start": "10:00", "precision": "exact"},
                  "issues": []
                }
              ]
            }
            """;

        var result = MemoResponseMapper.Map(json, "test", request);

        var candidate = Assert.Single(result.Candidates);
        Assert.Null(candidate.RemindAtUtc);
        Assert.NotNull(candidate.WorkTime);
        Assert.Equal(new DateOnly(2026, 10, 24), candidate.WorkTime!.Date);
        Assert.Contains(candidate.Issues, issue => issue.Contains("已发生工作", StringComparison.Ordinal));
    }

    [Fact]
    public void Map_DateOnlyReminderIsNotTurnedIntoMidnight()
    {
        var request = Request("明天提交报表");
        var json = """
            {"items":[{"content":"提交报表","sourceText":"明天提交报表","remindText":"明天","remindAt":"2026-10-26"}]}
            """;

        var result = MemoResponseMapper.Map(json, "test", request);

        Assert.Null(Assert.Single(result.Candidates).RemindAtUtc);
    }

    [Fact]
    public void Map_SplitsTwoDistinctReminders()
    {
        var request = Request("明天下午三点提交报表，后天上午十点联系张工");
        var json = """
            {
              "items": [
                {"content": "提交报表", "sourceText": "明天下午三点提交报表", "remindText": "明天下午三点", "remindAt": "2026-10-26T15:00:00+08:00"},
                {"content": "联系张工", "sourceText": "后天上午十点联系张工", "remindText": "后天上午十点", "remindAt": "2026-10-27T10:00:00+08:00"}
              ]
            }
            """;

        var result = MemoResponseMapper.Map(json, "test", request);
        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, candidate => Assert.NotNull(candidate.RemindAtUtc));
    }

    [Fact]
    public void Normalize_MergesMultipleCandidatesWithoutDistinctReminders()
    {
        var candidates = new[]
        {
            new MemoParseCandidate { Content = "第一段", Tags = { "a" } },
            new MemoParseCandidate { Content = "第二段", Tags = { "b" } },
        };

        var normalized = MemoCandidateNormalizer.Normalize(candidates, Guid.NewGuid(), 1);

        var single = Assert.Single(normalized);
        Assert.Contains("第一段", single.Content);
        Assert.Contains("第二段", single.Content);
        Assert.Contains("a", single.Tags);
        Assert.Contains("b", single.Tags);
    }

    [Fact]
    public void Normalize_AssignsStableIdsPerOperation()
    {
        var captureId = Guid.NewGuid();
        var candidates = new[]
        {
            new MemoParseCandidate { Content = "一", RemindAtUtc = Captured.AddHours(1) },
            new MemoParseCandidate { Content = "二", RemindAtUtc = Captured.AddHours(2) },
        };

        var first = MemoCandidateNormalizer.Normalize(candidates, captureId, 3);
        var second = MemoCandidateNormalizer.Normalize(candidates, captureId, 3);

        Assert.Equal(first.Select(c => c.SourceId), second.Select(c => c.SourceId));
        Assert.NotEqual(first[0].SourceId, first[1].SourceId);
    }

    private static AiParseRequest Request(string raw) =>
        new(raw, Captured, "Asia/Shanghai", MemoParsePrompt.Version, MemoParsePrompt.SchemaVersion);
}
