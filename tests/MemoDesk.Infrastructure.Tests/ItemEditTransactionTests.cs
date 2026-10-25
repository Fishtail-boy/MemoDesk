using MemoDesk.Core.Items;
using MemoDesk.Core.Reminders;
using MemoDesk.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Infrastructure.Tests;

public sealed class ItemEditTransactionTests
{
    [Fact]
    public void Update_StaleRowVersion_ThrowsAndPreservesNewerContent()
    {
        using var db = new TestDatabase();
        var item = db.SeedItem(TestDatabase.Memo("旧内容", new DateOnly(2026, 10, 25)));

        db.Items.Update(item with { Content = "新内容", UpdatedAtUtc = TestDatabase.Now });

        Assert.Throws<ItemVersionConflictException>(() =>
            db.Items.Update(item with { Content = "旧窗口内容", UpdatedAtUtc = TestDatabase.Now }));
        Assert.Equal("新内容", db.Items.GetById(item.Id)!.Content);
    }

    [Fact]
    public void UpdateWithReminder_UnchangedLeavesExistingPlanUntouched()
    {
        using var db = new TestDatabase();
        var reminderAt = TestDatabase.Now.AddHours(1).AddSeconds(37);
        var item = db.SeedItem(
            TestDatabase.Memo("只改文字", new DateOnly(2026, 10, 25)),
            TestDatabase.Reminder(Guid.Empty, reminderAt));
        var plan = db.Reminders.GetActiveForItem(item.Id)!;
        var current = db.Items.GetById(item.Id)!;

        var result = db.Items.UpdateWithReminder(
            current with { Content = "只改文字（新）", UpdatedAtUtc = TestDatabase.Now },
            new ReminderEditIntent(ReminderEditKind.Unchanged, null, null, null, true),
            TestDatabase.Now);

        Assert.True(result.IsSuccess);
        Assert.Equal("只改文字（新）", db.Items.GetById(item.Id)!.Content);
        var unchanged = db.Reminders.GetActiveForItem(item.Id)!;
        Assert.Equal(plan.Id, unchanged.Id);
        Assert.Equal(plan.PlanVersion, unchanged.PlanVersion);
        Assert.Equal(reminderAt, unchanged.ReminderAtUtc);
        Assert.Empty(db.Reminders.GetPendingCancellations(10));
    }

    [Fact]
    public void UpdateWithReminder_ReminderVersionConflictRollsBackContent()
    {
        using var db = new TestDatabase();
        var original = TestDatabase.Now.AddHours(1);
        var item = db.SeedItem(
            TestDatabase.Memo("原内容", new DateOnly(2026, 10, 25)),
            TestDatabase.Reminder(Guid.Empty, original));
        var plan = db.Reminders.GetActiveForItem(item.Id)!;
        db.Reminders.SetReminder(item.Id, TestDatabase.Now.AddHours(2), TestDatabase.Now);
        var current = db.Items.GetById(item.Id)!;

        var result = db.Items.UpdateWithReminder(
            current with { Content = "旧窗口内容", UpdatedAtUtc = TestDatabase.Now },
            new ReminderEditIntent(ReminderEditKind.Set, TestDatabase.Now.AddHours(3), plan.Id, plan.PlanVersion, true),
            TestDatabase.Now);

        Assert.Equal(MemoEditOutcome.ReminderVersionConflict, result.Outcome);
        Assert.Equal("原内容", db.Items.GetById(item.Id)!.Content);
        Assert.Equal(TestDatabase.Now.AddHours(2), db.Reminders.GetActiveForItem(item.Id)!.ReminderAtUtc);
        Assert.Equal(1, db.Items.GetById(item.Id)!.RowVersion);
    }

    [Fact]
    public void UpdateWithReminder_ReminderWriteFailureRollsBackContent()
    {
        using var db = new TestDatabase();
        var item = db.SeedItem(
            TestDatabase.Memo("原内容", new DateOnly(2026, 10, 25)),
            TestDatabase.Reminder(Guid.Empty, TestDatabase.Now.AddHours(1)));
        var plan = db.Reminders.GetActiveForItem(item.Id)!;
        var current = db.Items.GetById(item.Id)!;

        using (var connection = db.Factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fail_reminder_update
                BEFORE UPDATE ON reminders
                BEGIN
                    SELECT RAISE(ABORT, 'synthetic reminder failure');
                END;
                """;
            command.ExecuteNonQuery();
        }

        var result = db.Items.UpdateWithReminder(
            current with { Content = "新内容", UpdatedAtUtc = TestDatabase.Now },
            new ReminderEditIntent(ReminderEditKind.Set, TestDatabase.Now.AddHours(2), plan.Id, plan.PlanVersion, true),
            TestDatabase.Now);

        Assert.Equal(MemoEditOutcome.Failure, result.Outcome);
        Assert.Equal("原内容", db.Items.GetById(item.Id)!.Content);
        Assert.Equal(TestDatabase.Now.AddHours(1), db.Reminders.GetActiveForItem(item.Id)!.ReminderAtUtc);
        Assert.Equal(1, db.Items.GetById(item.Id)!.RowVersion);
    }
}
