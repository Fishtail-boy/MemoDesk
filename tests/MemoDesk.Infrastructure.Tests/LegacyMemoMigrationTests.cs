using MemoDesk.Core;
using MemoDesk.Core.Capturing;
using MemoDesk.Core.Drafting;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Timeline;
using MemoDesk.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemoDesk.Infrastructure.Tests;

public sealed class LegacyMemoMigrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CaptureWithUsableDraft_BecomesMemoAndAppliedMarker()
    {
        using var db = new TestDatabase();
        var capture = db.SeedCapture("明天三点提交报表");
        var candidate = new DraftCandidate
        {
            Type = ItemType.Todo,
            Content = "提交报表",
            BusinessDate = new DateOnly(2026, 10, 26),
            RemindAtUtc = Now.AddDays(1),
        };
        db.Drafts.Add(new ParseDraft(
            Guid.NewGuid(), capture.Id, 1, DraftStatus.AwaitingConfirmation,
            new[] { candidate }, null, null, "p1", "v1", 0, null, Now, Now));

        Migrate(db);

        var item = Assert.Single(db.Queries.QueryItems("capture_id = $captureId", ("$captureId", capture.Id.ToString("N"))));
        Assert.Equal(ItemType.Todo, item.Type);
        Assert.Equal("提交报表", item.Content);
        Assert.NotNull(item.MemoDate);
        Assert.Equal(capture.CapturedAtUtc, item.CreatedAtUtc);

        var draft = db.Drafts.GetLatestForCapture(capture.Id)!;
        Assert.Equal(DraftStatus.Applied, draft.Status);
        Assert.NotNull(draft.AppliedAtUtc);

        var counts = Counts(db);
        Assert.Equal(1L, counts.Items);
        Assert.Equal(1L, counts.CapturedEvents);
        Assert.Equal(1L, counts.Reminders);
        Assert.Equal("converted-draft", ContextState(db, capture.Id));
    }

    [Fact]
    public void EditedDraft_UsesPersistedHumanContentAndRemovedCandidatesStayRemoved()
    {
        using var db = new TestDatabase();
        var capture = db.SeedCapture("原始输入");
        var keep = new DraftCandidate
        {
            Type = ItemType.Memo,
            Content = "AI 内容",
            BusinessDate = new DateOnly(2026, 10, 25),
        };
        var remove = new DraftCandidate
        {
            Type = ItemType.Todo,
            Content = "应被移除",
            BusinessDate = new DateOnly(2026, 10, 25),
        };
        var draft = new ParseDraft(
            Guid.NewGuid(), capture.Id, 1, DraftStatus.AwaitingConfirmation,
            new[] { keep, remove }, null, null, "p1", "v1", 0, null, Now, Now);
        db.Drafts.Add(draft);

        var edited = new DraftEditState
        {
            Candidates =
            {
                new DraftCandidateEditState
                {
                    SourceId = keep.SourceId,
                    Type = ItemType.Memo,
                    Content = "人工修改后的内容",
                    Date = new DateOnly(2026, 10, 25),
                    TagsText = "人工, 修改",
                },
            },
        };
        Assert.True(db.Drafts.SaveEditState(draft.Id, 0, JsonDefaults.Serialize(edited), Now));

        Migrate(db);

        var item = Assert.Single(db.Queries.QueryItems("capture_id = $captureId", ("$captureId", capture.Id.ToString("N"))));
        Assert.Equal("人工修改后的内容", item.Content);
        Assert.True(item.HumanRevised);
        Assert.Contains("人工", item.Tags);
        Assert.DoesNotContain("应被移除", db.Queries.QueryItems("capture_id = $captureId", ("$captureId", capture.Id.ToString("N"))).Select(i => i.Content));
    }

    [Fact]
    public void CaptureWithExistingItem_IsKeptAndNotDuplicated()
    {
        using var db = new TestDatabase();
        var capture = db.SeedCapture("已有事项");
        var item = db.SeedItem(TestDatabase.Memo("已有备忘", new DateOnly(2026, 10, 25)), capture: capture);

        Migrate(db);
        Migrate(db);

        var items = db.Queries.QueryItems("capture_id = $captureId", ("$captureId", capture.Id.ToString("N")));
        Assert.Single(items);
        Assert.Equal(item.Id, items[0].Id);
        Assert.Equal("kept-existing", ContextState(db, capture.Id));
        Assert.Equal(CaptureParseStatus.Parsed, db.Captures.GetById(capture.Id)!.ParseStatus);
        Assert.Equal(1L, Counts(db).CapturedEvents);
    }

    [Fact]
    public void CaptureWithoutDraft_GetsRawMemoAndKeepsPendingParse()
    {
        using var db = new TestDatabase();
        var capture = db.SeedCapture("  没有草稿的原文  ");

        Migrate(db);

        var item = Assert.Single(db.Queries.QueryItems("capture_id = $captureId", ("$captureId", capture.Id.ToString("N"))));
        Assert.Equal(ItemType.Memo, item.Type);
        Assert.Equal("没有草稿的原文", item.Content);
        Assert.Equal(CaptureParseStatus.Pending, db.Captures.GetById(capture.Id)!.ParseStatus);
        Assert.Equal("created-raw", ContextState(db, capture.Id));
    }

    [Fact]
    public void DiscardedDraft_DoesNotCreateMemo()
    {
        using var db = new TestDatabase();
        var capture = db.SeedCapture("用户丢弃");
        db.Drafts.Add(new ParseDraft(
            Guid.NewGuid(), capture.Id, 1, DraftStatus.Discarded,
            Array.Empty<DraftCandidate>(), null, null, "p1", "v1", 0, null, Now, Now));

        Migrate(db);

        Assert.Empty(db.Queries.QueryItems("capture_id = $captureId", ("$captureId", capture.Id.ToString("N"))));
        Assert.Equal("skipped-discarded", ContextState(db, capture.Id));
    }

    private static void Migrate(TestDatabase db) =>
        new LegacyMemoMigration(db.Factory, new Clock(Now), NullLogger<LegacyMemoMigration>.Instance).Run();

    private static string? ContextState(TestDatabase db, Guid captureId)
    {
        using var connection = db.Factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM migration_context WHERE capture_id = $id;";
        command.Parameters.AddWithValue("$id", captureId.ToString("N"));
        return command.ExecuteScalar() as string;
    }

    private static (long Items, long CapturedEvents, long Reminders) Counts(TestDatabase db)
    {
        using var connection = db.Factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM items),
                (SELECT COUNT(*) FROM timeline_events WHERE event_type = 'MemoCaptured'),
                (SELECT COUNT(*) FROM reminders);
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
