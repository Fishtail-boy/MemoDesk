using MemoDesk.Core.Drafting;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Timeline;
using MemoDesk.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Infrastructure.Tests;

public sealed class SqliteItemStoreTests
{
    [Fact]
    public void ConfirmDraft_PersistsItemsRemindersAndConfirmStatus()
    {
        using var db = new TestDatabase();
        var capture = db.SeedCapture();
        var draft = CreateDraft(db, capture.Id);
        var item = TestDatabase.Todo("复核登录故障", new DateOnly(2026, 10, 22));
        var reminder = TestDatabase.Reminder(item.Id, TestDatabase.Now.AddHours(2));

        var outcome = db.Items.ConfirmDraft(draft.Id, 1, new[] { item }, new[] { reminder }, Array.Empty<TimelineEvent>(), TestDatabase.Now);

        Assert.Equal(DraftConfirmationOutcome.Confirmed, outcome);
        Assert.NotNull(db.Items.GetById(item.Id));
        Assert.NotNull(db.Reminders.GetActiveForItem(item.Id));
        Assert.Equal(DraftStatus.Confirmed, db.Drafts.GetById(draft.Id)!.Status);
    }

    [Fact]
    public void ConfirmDraft_SecondConfirm_ReturnsAlreadyConfirmed()
    {
        using var db = new TestDatabase();
        var capture = db.SeedCapture();
        var draft = CreateDraft(db, capture.Id);
        var item = TestDatabase.Memo("备忘", new DateOnly(2026, 10, 21));

        Assert.Equal(
            DraftConfirmationOutcome.Confirmed,
            db.Items.ConfirmDraft(draft.Id, 1, new[] { item }, Array.Empty<ReminderPlan>(), Array.Empty<TimelineEvent>(), TestDatabase.Now));

        var second = db.Items.ConfirmDraft(draft.Id, 1, new[] { item }, Array.Empty<ReminderPlan>(), Array.Empty<TimelineEvent>(), TestDatabase.Now);

        Assert.Equal(DraftConfirmationOutcome.AlreadyConfirmed, second);
    }

    [Fact]
    public void ConfirmDraft_VersionConflict_DoesNotWrite()
    {
        using var db = new TestDatabase();
        var capture = db.SeedCapture();
        var draft = CreateDraft(db, capture.Id);
        var item = TestDatabase.Memo("备忘", new DateOnly(2026, 10, 21));

        var outcome = db.Items.ConfirmDraft(draft.Id, 2, new[] { item }, Array.Empty<ReminderPlan>(), Array.Empty<TimelineEvent>(), TestDatabase.Now);

        Assert.Equal(DraftConfirmationOutcome.VersionConflict, outcome);
        Assert.Null(db.Items.GetById(item.Id));
    }

    [Fact]
    public void ConfirmDraft_DuplicateCandidate_RollsBackWholeTransaction()
    {
        using var db = new TestDatabase();
        var capture = db.SeedCapture();
        var draft = CreateDraft(db, capture.Id);
        var sourceId = Guid.NewGuid();
        var first = TestDatabase.Todo("第一条") with { CaptureId = capture.Id, CandidateSourceId = sourceId };
        var second = TestDatabase.Memo("第二条", new DateOnly(2026, 10, 21)) with { CaptureId = capture.Id, CandidateSourceId = sourceId };

        Assert.ThrowsAny<SqliteException>(() =>
            db.Items.ConfirmDraft(draft.Id, 1, new[] { first, second }, Array.Empty<ReminderPlan>(), Array.Empty<TimelineEvent>(), TestDatabase.Now));

        Assert.Null(db.Items.GetById(first.Id));
        Assert.Null(db.Items.GetById(second.Id));
        Assert.Equal(DraftStatus.AwaitingConfirmation, db.Drafts.GetById(draft.Id)!.Status);
    }

    [Fact]
    public void CompleteItem_DisablesReminderAndWritesTimelineEvent()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("完成我", new DateOnly(2026, 10, 21));
        var reminder = TestDatabase.Reminder(item.Id, TestDatabase.Now.AddHours(1));
        var seeded = db.SeedItem(item, reminder);

        var completedAt = TestDatabase.Now.AddMinutes(30);
        Assert.True(db.Items.TrySetStatus(seeded.Id, ItemStatus.Completed, completedAt));

        var reloaded = db.Items.GetById(seeded.Id)!;
        Assert.Equal(ItemStatus.Completed, reloaded.Status);
        Assert.Equal(completedAt, reloaded.CompletedAtUtc);
        Assert.Null(db.Reminders.GetActiveForItem(seeded.Id));

        using var connection = db.Factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM timeline_events WHERE item_id = $id AND event_type = 'ItemCompleted';";
        command.Parameters.AddWithValue("$id", seeded.Id.ToString("N"));
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CompleteItem_WithOccurrence_MarksOccurrenceCompleted()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("从通知完成", new DateOnly(2026, 10, 21));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddHours(1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;
        var occurrence = new ReminderOccurrence(
            Guid.NewGuid(), plan.Id, seeded.Id, plan.ReminderAtUtc, ReminderOccurrenceStatus.Pending,
            "tag", SnoozeOption.Defaults, null, null, null, 0, null, TestDatabase.Now, TestDatabase.Now,
            PlanVersion: plan.PlanVersion);
        db.Reminders.TryAddOccurrence(occurrence);

        Assert.True(db.Items.TrySetStatus(seeded.Id, ItemStatus.Completed, TestDatabase.Now, occurrence.Id));

        var reloaded = db.Reminders.GetOccurrence(occurrence.Id)!;
        Assert.Equal(ReminderOccurrenceStatus.Completed, reloaded.Status);
        Assert.Equal("completed", reloaded.Action);
    }

    [Fact]
    public void ReopenItem_KeepsReminderDisabled()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("重新打开", new DateOnly(2026, 10, 21));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddHours(1)));

        Assert.True(db.Items.TrySetStatus(seeded.Id, ItemStatus.Completed, TestDatabase.Now));
        Assert.True(db.Items.TrySetStatus(seeded.Id, ItemStatus.Open, TestDatabase.Now.AddMinutes(5)));

        var reloaded = db.Items.GetById(seeded.Id)!;
        Assert.Equal(ItemStatus.Open, reloaded.Status);
        Assert.Null(reloaded.CompletedAtUtc);
        Assert.Null(db.Reminders.GetActiveForItem(seeded.Id));
    }

    [Fact]
    public void ReopenArchivedMemo_Works()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Memo("归档后备忘", new DateOnly(2026, 10, 21));
        var seeded = db.SeedItem(item);

        Assert.True(db.Items.TrySetStatus(seeded.Id, ItemStatus.Archived, TestDatabase.Now));
        Assert.True(db.Items.TrySetStatus(seeded.Id, ItemStatus.Open, TestDatabase.Now.AddMinutes(5)));

        var reloaded = db.Items.GetById(seeded.Id)!;
        Assert.Equal(ItemStatus.Open, reloaded.Status);
        Assert.Null(reloaded.ArchivedAtUtc);
    }

    private static ParseDraft CreateDraft(TestDatabase db, Guid captureId)
    {
        var draft = new ParseDraft(
            Guid.NewGuid(), captureId, 1, DraftStatus.AwaitingConfirmation,
            Array.Empty<DraftCandidate>(), null, "m", "p1", "v1", 0, null,
            TestDatabase.Now, TestDatabase.Now);
        db.Drafts.Add(draft);
        return draft;
    }
}
