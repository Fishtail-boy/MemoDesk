using MemoDesk.Core;
using MemoDesk.Core.Capturing;
using MemoDesk.Core.Parsing;
using MemoDesk.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemoDesk.Infrastructure.Tests;

public sealed class MemoParseCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Process_AppliesSingleMemoAndFutureReminder()
    {
        using var db = new TestDatabase();
        var capture = new MemoCaptureService(new SqliteMemoCaptureStore(db.Factory), new Clock(Now))
            .Capture("明天下午三点提交报表", "Asia/Shanghai").Capture;
        var parser = new FakeParser(request => new MemoParseResponse(
            "{}",
            "fake",
            new[]
            {
                new MemoParseCandidate
                {
                    Content = "提交报表",
                    SourceText = "明天下午三点提交报表",
                    RemindText = "明天下午三点",
                    RemindAtUtc = request.CapturedAtUtc.AddHours(29),
                    Tags = { "工作" },
                },
            },
            Array.Empty<string>()));

        await Coordinator(db, parser).ProcessAsync(capture.Id, CancellationToken.None);

        var item = db.Queries.QueryItems("capture_id = $captureId", ("$captureId", capture.Id.ToString("N"))).Single();
        Assert.Equal("提交报表", item.Content);
        Assert.Contains("工作", item.Tags);
        Assert.NotNull(db.Reminders.GetActiveForItem(item.Id));
        Assert.Equal(CaptureParseStatus.Parsed, db.Captures.GetById(capture.Id)!.ParseStatus);
    }

    [Fact]
    public async Task Process_ParserFailureKeepsRawMemo()
    {
        using var db = new TestDatabase();
        var capture = new MemoCaptureService(new SqliteMemoCaptureStore(db.Factory), new Clock(Now))
            .Capture("没有 Key 的原文", "Asia/Shanghai").Capture;
        var parser = new FakeParser(_ => throw new AiParseException(AiFailureKind.Configuration, "未配置 Key"));

        await Coordinator(db, parser).ProcessAsync(capture.Id, CancellationToken.None);

        var item = db.Queries.QueryItems("capture_id = $captureId", ("$captureId", capture.Id.ToString("N"))).Single();
        Assert.Equal("没有 Key 的原文", item.Content);
        Assert.Equal(CaptureParseStatus.Failed, db.Captures.GetById(capture.Id)!.ParseStatus);
        Assert.Equal("Failed", ParseStatus(db, capture.Id));
    }

    [Fact]
    public async Task Process_HumanEditBeforeApplyIsNotOverwritten()
    {
        using var db = new TestDatabase();
        var capture = new MemoCaptureService(new SqliteMemoCaptureStore(db.Factory), new Clock(Now))
            .Capture("原始内容", "Asia/Shanghai").Capture;
        var item = db.Queries.QueryItems("capture_id = $captureId", ("$captureId", capture.Id.ToString("N"))).Single();
        SetHumanRevised(db, item.Id);

        var parser = new FakeParser(_ => new MemoParseResponse(
            "{}",
            "fake",
            new[] { new MemoParseCandidate { Content = "AI 内容" } },
            Array.Empty<string>()));

        await Coordinator(db, parser).ProcessAsync(capture.Id, CancellationToken.None);

        Assert.Equal("原始内容", db.Items.GetById(item.Id)!.Content);
        Assert.Equal("Skipped", ParseStatus(db, capture.Id));
    }

    [Fact]
    public async Task Process_SavedAwaitingApplyResultIsAppliedWithoutCallingAiAgain()
    {
        using var db = new TestDatabase();
        var capture = new MemoCaptureService(new SqliteMemoCaptureStore(db.Factory), new Clock(Now))
            .Capture("已经保存结果", "Asia/Shanghai").Capture;
        var item = db.Queries.QueryItems("capture_id = $captureId", ("$captureId", capture.Id.ToString("N"))).Single();
        var result = new MemoParseRecord(
            Guid.NewGuid(), capture.Id, 1, 1, MemoParseStatus.AwaitingApply,
            new[] { new MemoParseCandidate { SourceId = StableId.Candidate(capture.Id, 1, 0), Content = "已保存结果" } },
            "{}", "fake", MemoParsePrompt.Version, MemoParsePrompt.SchemaVersion, 1, null, Now, Now);
        new SqliteMemoParseRepository(db.Factory).Add(result);

        var parser = new FakeParser(_ => throw new InvalidOperationException("不应再次调用 AI"));
        await Coordinator(db, parser).ProcessAsync(capture.Id, CancellationToken.None);

        Assert.Equal(0, parser.Calls);
        Assert.Equal("已保存结果", db.Items.GetById(item.Id)!.Content);
        Assert.Equal("Applied", ParseStatus(db, capture.Id));
    }

    [Fact]
    public async Task Retry_AfterFailure_StartsNewOperationAndApplies()
    {
        using var db = new TestDatabase();
        var capture = new MemoCaptureService(new SqliteMemoCaptureStore(db.Factory), new Clock(Now))
            .Capture("明天下午三点重试内容", "Asia/Shanghai").Capture;
        var parser = new SequenceParser(
            _ => throw new AiParseException(AiFailureKind.Configuration, "未配置 Key"),
            request => new MemoParseResponse(
                "{}",
                "fake",
                new[]
                {
                    new MemoParseCandidate
                    {
                        Content = "重试后的内容",
                        SourceText = "明天下午三点重试内容",
                        RemindText = "明天下午三点",
                        RemindAtUtc = request.CapturedAtUtc.AddHours(29),
                    },
                },
                Array.Empty<string>()));

        var coordinator = Coordinator(db, parser);
        await coordinator.ProcessAsync(capture.Id, CancellationToken.None);
        Assert.Equal(CaptureParseStatus.Failed, db.Captures.GetById(capture.Id)!.ParseStatus);

        coordinator.Retry(capture.Id);
        await coordinator.ProcessAsync(capture.Id, CancellationToken.None);

        var item = db.Queries.QueryItems("capture_id = $captureId", ("$captureId", capture.Id.ToString("N"))).Single();
        Assert.Equal("重试后的内容", item.Content);
        Assert.Equal(2, item.RowVersion);
        Assert.NotNull(db.Reminders.GetActiveForItem(item.Id));
        Assert.Equal(CaptureParseStatus.Parsed, db.Captures.GetById(capture.Id)!.ParseStatus);
    }

    [Fact]
    public async Task Process_TwoDistinctRemindersCreatesTwoMemos()
    {
        using var db = new TestDatabase();
        var capture = new MemoCaptureService(new SqliteMemoCaptureStore(db.Factory), new Clock(Now))
            .Capture("明天下午三点第一件，明天下午四点第二件", "Asia/Shanghai").Capture;
        var parser = new FakeParser(request => new MemoParseResponse(
            "{}",
            "fake",
            new[]
            {
                new MemoParseCandidate
                {
                    Content = "第一件",
                    SourceText = "明天下午三点第一件",
                    RemindText = "明天下午三点",
                    RemindAtUtc = request.CapturedAtUtc.AddHours(29),
                },
                new MemoParseCandidate
                {
                    Content = "第二件",
                    SourceText = "明天下午四点第二件",
                    RemindText = "明天下午四点",
                    RemindAtUtc = request.CapturedAtUtc.AddHours(30),
                },
            },
            Array.Empty<string>()));

        await Coordinator(db, parser).ProcessAsync(capture.Id, CancellationToken.None);

        var items = db.Queries.QueryItems("capture_id = $captureId ORDER BY created_at_utc, rowid", ("$captureId", capture.Id.ToString("N")));
        Assert.Equal(2, items.Count);
        Assert.Equal(2, items.Count(item => db.Reminders.GetActiveForItem(item.Id) is not null));
    }

    [Fact]
    public void Recover_PagesAllPendingCapturesBeyondOneBatch()
    {
        using var db = new TestDatabase();
        using (var connection = db.Factory.Create())
        using (var transaction = connection.BeginTransaction())
        {
            for (var index = 0; index < 1001; index++)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO captures (
                        id, raw_text, captured_at_utc, captured_time_zone_id,
                        parse_status, recorded_at_utc, parse_operation_version)
                    VALUES ($id, 'bulk', $at, 'Asia/Shanghai', 'Pending', $at, 1);
                    """;
                var id = Guid.NewGuid().ToString("N");
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$at", Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        var coordinator = Coordinator(db, new FakeParser(_ => throw new InvalidOperationException("no parse")));
        typeof(MemoParseCoordinator)
            .GetMethod("Recover", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(coordinator, null);
        var queue = (System.Threading.Channels.Channel<Guid>)typeof(MemoParseCoordinator)
            .GetField("_queue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!;

        Assert.Equal(1001, queue.Reader.Count);
    }

    private static MemoParseCoordinator Coordinator(TestDatabase db, IMemoAiParser parser) =>
        new(
            parser,
            db.Captures,
            new SqliteMemoParseRepository(db.Factory),
            new SqliteMemoApplyStore(db.Factory),
            new Clock(Now),
            NullLogger<MemoParseCoordinator>.Instance);

    private static void SetHumanRevised(TestDatabase db, Guid itemId)
    {
        using var connection = db.Factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET human_revised = 1 WHERE id = $id;";
        command.Parameters.AddWithValue("$id", itemId.ToString("N"));
        command.ExecuteNonQuery();
    }

    private static string? ParseStatus(TestDatabase db, Guid captureId)
    {
        using var connection = db.Factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status FROM memo_parse_results
            WHERE capture_id = $id
            ORDER BY version DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", captureId.ToString("N"));
        return command.ExecuteScalar() as string;
    }

    private sealed class FakeParser(Func<AiParseRequest, MemoParseResponse> response) : IMemoAiParser
    {
        public int Calls { get; private set; }

        public Task<MemoParseResponse> ParseMemoAsync(AiParseRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(response(request));
        }
    }

    private sealed class SequenceParser(params Func<AiParseRequest, MemoParseResponse>[] responses) : IMemoAiParser
    {
        private int _index;

        public Task<MemoParseResponse> ParseMemoAsync(AiParseRequest request, CancellationToken cancellationToken)
        {
            var response = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return Task.FromResult(response(request));
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
