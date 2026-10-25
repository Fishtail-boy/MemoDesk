using MemoDesk.Core.Domain;
using MemoDesk.Core.Parsing;

namespace MemoDesk.Core.Tests.Parsing;

public sealed class AiResponseMapperTests
{
    private static readonly AiParseRequest Request = new(
        RawText: "原始输入",
        CapturedAtUtc: new DateTimeOffset(2026, 10, 21, 6, 0, 0, TimeSpan.Zero),
        TimeZoneId: "Asia/Shanghai",
        PromptVersion: "p1",
        SchemaVersion: "v1");

    [Fact]
    public void Map_ProducesTodosWorkLogsAndMemos()
    {
        const string json = """
            {
              "items": [
                {"type":"todo","content":"复核登录故障","businessDate":"2026-10-22","remindAt":"2026-10-22T15:00:00+08:00","priority":"high","tags":["故障","复核"]},
                {"type":"work_log","content":"处理登录故障","workTime":{"date":"2026-10-21","start":"09:10","end":null,"precision":"exact"}},
                {"type":"memo","content":"客户要求保留原始日志"}
              ],
              "notes":["已拆分三类事项"]
            }
            """;

        var response = AiResponseMapper.Map(json, "deepseek-flash", Request);

        Assert.Equal(3, response.Candidates.Count);
        Assert.Single(response.Notes);

        var todo = response.Candidates[0];
        Assert.Equal(ItemType.Todo, todo.Type);
        Assert.Equal(new DateOnly(2026, 10, 22), todo.BusinessDate);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 7, 0, 0, TimeSpan.Zero), todo.RemindAtUtc);
        Assert.Equal(ItemPriority.High, todo.Priority);
        Assert.Equal(new[] { "故障", "复核" }, todo.Tags);

        var workLog = response.Candidates[1];
        Assert.Equal(ItemType.WorkLog, workLog.Type);
        Assert.NotNull(workLog.WorkTime);
        Assert.Equal(WorkTimePrecision.Exact, workLog.WorkTime!.Precision);
        Assert.Equal(new TimeOnly(9, 10), workLog.WorkTime.Start);
        Assert.Null(workLog.RemindAtUtc);

        var memo = response.Candidates[2];
        Assert.Equal(ItemType.Memo, memo.Type);
        Assert.Equal(new DateOnly(2026, 10, 21), memo.BusinessDate); // 默认捕获当天
    }

    [Fact]
    public void Map_StripsReminderFromWorkLog()
    {
        const string json = """
            {"items":[{"type":"work_log","content":"写文档","remindAt":"2026-10-22T09:00:00+08:00"}]}
            """;

        var response = AiResponseMapper.Map(json, "m", Request);

        Assert.Null(Assert.Single(response.Candidates).RemindAtUtc);
    }

    [Fact]
    public void Map_TreatsTimeWithoutOffsetAsCapturedTimeZone()
    {
        const string json = """
            {"items":[{"type":"todo","content":"提交材料","remindAt":"2026-10-22 15:00:00"}]}
            """;

        var response = AiResponseMapper.Map(json, "m", Request);

        Assert.Equal(
            new DateTimeOffset(2026, 10, 22, 7, 0, 0, TimeSpan.Zero),
            Assert.Single(response.Candidates).RemindAtUtc);
    }

    [Fact]
    public void Map_MarksPastReminderAsIssue()
    {
        const string json = """
            {"items":[{"type":"todo","content":"过期提醒","remindAt":"2026-10-20T09:00:00+08:00"}]}
            """;

        var response = AiResponseMapper.Map(json, "m", Request);

        Assert.Contains(response.Candidates[0].Issues, issue => issue.Contains("早于捕获时间"));
    }

    [Fact]
    public void Map_InfersNextDayForOvernightRange()
    {
        const string json = """
            {"items":[{"type":"work_log","content":"夜间值守","workTime":{"date":"2026-10-21","start":"22:00","end":"02:00","precision":"range"}}]}
            """;

        var response = AiResponseMapper.Map(json, "m", Request);

        var workTime = Assert.Single(response.Candidates).WorkTime!;
        Assert.Equal(WorkTimePrecision.Range, workTime.Precision);
        Assert.Equal(new DateOnly(2026, 10, 22), workTime.EndDate);
    }

    [Fact]
    public void Map_AcceptsJsonInsideCodeFence()
    {
        const string json = """
            ```json
            {"items":[{"type":"memo","content":"代码围栏"}]}
            ```
            """;

        var response = AiResponseMapper.Map(json, "m", Request);

        Assert.Equal("代码围栏", Assert.Single(response.Candidates).Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"items\":[{\"type\":\"unknown\",\"content\":\"x\"}]}")]
    public void Map_InvalidContent_ThrowsInvalidResponse(string json)
    {
        var exception = Assert.Throws<AiParseException>(() => AiResponseMapper.Map(json, "m", Request));
        Assert.Equal(AiFailureKind.InvalidResponse, exception.Kind);
    }

    [Fact]
    public void Map_TooManyItems_Throws()
    {
        var items = string.Join(',', Enumerable.Range(0, 11).Select(i => $"{{\"type\":\"memo\",\"content\":\"事项{i}\"}}"));

        var exception = Assert.Throws<AiParseException>(() => AiResponseMapper.Map($"{{\"items\":[{items}]}}", "m", Request));
        Assert.Equal(AiFailureKind.InvalidResponse, exception.Kind);
    }
}
