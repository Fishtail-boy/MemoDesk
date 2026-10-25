using MemoDesk.Core.Capturing;
using MemoDesk.Core.Domain;
using MemoDesk.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Infrastructure.Tests;

public sealed class MemoCaptureTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Capture_SavesCaptureMemoAndTimelineEventInOneTransaction()
    {
        using var db = new TestDatabase();
        var service = new MemoCaptureService(new SqliteMemoCaptureStore(db.Factory), new Clock(Now));

        var result = service.Capture("  明天三点提交报表  ", "Asia/Shanghai");

        Assert.False(result.AlreadyExisted);
        Assert.Equal("明天三点提交报表", result.Memo.Content);
        Assert.Equal(ItemType.Memo, result.Memo.Type);
        Assert.Equal(ItemStatus.Open, result.Memo.Status);
        Assert.Equal(new DateOnly(2026, 10, 25), result.Memo.MemoDate);
        Assert.Equal(result.Capture.Id, result.Memo.CaptureId);
        Assert.Equal(result.Capture.CapturedAtUtc, result.Memo.CreatedAtUtc);
        Assert.Equal("  明天三点提交报表  ", db.Captures.GetById(result.Capture.Id)!.RawText);

        using var connection = db.Factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_type, occurred_at_utc, content_snapshot
            FROM timeline_events
            WHERE item_id = $itemId;
            """;
        command.Parameters.AddWithValue("$itemId", result.Memo.Id.ToString("N"));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("MemoCaptured", reader.GetString(0));
        Assert.Equal(result.Capture.CapturedAtUtc, DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("明天三点提交报表", reader.GetString(2));
    }

    [Fact]
    public void Capture_RetryWithSameIds_ReturnsExistingWithoutDuplicates()
    {
        using var db = new TestDatabase();
        var store = new SqliteMemoCaptureStore(db.Factory);
        var service = new MemoCaptureService(store, new Clock(Now));
        var captureId = Guid.NewGuid();
        var memoId = Guid.NewGuid();

        var first = service.Capture("同一笔输入", "Asia/Shanghai", captureId, memoId);
        var second = service.Capture("同一笔输入", "Asia/Shanghai", captureId, memoId);

        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
        Assert.Equal(first.Memo.Id, second.Memo.Id);
        Assert.Single(db.Captures.GetByStatus(CaptureParseStatus.Pending, 10));
        Assert.Equal(1L, Count(db, "items"));
        Assert.Equal(1L, Count(db, "timeline_events"));
    }

    [Fact]
    public void Capture_MarksContextSoLegacyMigrationDoesNotConvertItAgain()
    {
        using var db = new TestDatabase();
        var service = new MemoCaptureService(new SqliteMemoCaptureStore(db.Factory), new Clock(Now));

        var result = service.Capture("新格式", "Asia/Shanghai");

        using var connection = db.Factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM migration_context WHERE capture_id = $id;";
        command.Parameters.AddWithValue("$id", result.Capture.Id.ToString("N"));
        Assert.Equal("current", command.ExecuteScalar());
    }

    private static long Count(TestDatabase db, string table)
    {
        using var connection = db.Factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
