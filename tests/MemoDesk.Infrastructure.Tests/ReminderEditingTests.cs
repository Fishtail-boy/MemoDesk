using MemoDesk.Core.Reminders;

namespace MemoDesk.Infrastructure.Tests;

public sealed class ReminderEditingTests
{
    [Fact]
    public void SetReminder_UpdatesPlanAndInvalidatesPendingOccurrence()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("改提醒", new DateOnly(2026, 10, 21));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddHours(1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;

        var occurrence = new ReminderOccurrence(
            Guid.NewGuid(), plan.Id, seeded.Id, plan.ReminderAtUtc, ReminderOccurrenceStatus.Pending,
            "tag", SnoozeOption.Defaults, null, null, null, 0, null, TestDatabase.Now, TestDatabase.Now,
            PlanVersion: plan.PlanVersion);
        db.Reminders.TryAddOccurrence(occurrence);

        var newTime = TestDatabase.Now.AddHours(3);
        db.Reminders.SetReminder(seeded.Id, newTime, TestDatabase.Now);

        var updatedPlan = db.Reminders.GetActiveForItem(seeded.Id)!;
        Assert.Equal(newTime, updatedPlan.ReminderAtUtc);
        Assert.Equal(2, updatedPlan.PlanVersion);

        var updatedOccurrence = db.Reminders.GetOccurrence(occurrence.Id)!;
        Assert.Equal(ReminderOccurrenceStatus.Cancelled, updatedOccurrence.Status);
        Assert.Equal("reminder-changed", updatedOccurrence.CancelReason);
    }

    [Fact]
    public void SetReminder_Null_DisablesPlan()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Memo("取消提醒", new DateOnly(2026, 10, 21));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddHours(1)));

        db.Reminders.SetReminder(seeded.Id, null, TestDatabase.Now);

        Assert.Null(db.Reminders.GetActiveForItem(seeded.Id));
    }

    [Fact]
    public void SetReminder_NoExistingPlan_CreatesOne()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("新提醒", new DateOnly(2026, 10, 21));
        var seeded = db.SeedItem(item);

        var at = TestDatabase.Now.AddHours(2);
        db.Reminders.SetReminder(seeded.Id, at, TestDatabase.Now);

        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;
        Assert.Equal(at, plan.ReminderAtUtc);
        Assert.Equal(1, plan.PlanVersion);
    }
}
