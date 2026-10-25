using MemoDesk.Core.Reminders;
using MemoDesk.Infrastructure.Queries;

namespace MemoDesk.Infrastructure.Tests;

public sealed class ReminderQueryServiceTests
{
    [Fact]
    public void Overview_WithoutOccurrence_IsUnreminded()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("尚未到点", new DateOnly(2026, 10, 23));
        db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddHours(2)));

        var row = Assert.Single(new ReminderQueryService(db.Factory).GetOverview());

        Assert.Null(row.OccurrenceStatus);
        Assert.True(ReminderQueryService.MatchesFilter(row, ReminderViewFilter.Unreminded));
        Assert.False(ReminderQueryService.MatchesFilter(row, ReminderViewFilter.Requested));
    }

    [Fact]
    public void Overview_ClassifiesRequestedSnoozedAndProblem()
    {
        using var db = new TestDatabase();
        var query = new ReminderQueryService(db.Factory);

        var requestedItem = TestDatabase.Todo("已交接", new DateOnly(2026, 10, 23));
        var requested = db.SeedItem(requestedItem, TestDatabase.Reminder(requestedItem.Id, TestDatabase.Now.AddMinutes(-1)));
        var requestedPlan = db.Reminders.GetActiveForItem(requested.Id)!;
        db.Reminders.TryAddOccurrence(new ReminderOccurrence(
            Guid.NewGuid(), requestedPlan.Id, requested.Id, requestedPlan.ReminderAtUtc,
            ReminderOccurrenceStatus.Requested, "tag", SnoozeOption.Defaults,
            TestDatabase.Now, null, null, 0, null, TestDatabase.Now, TestDatabase.Now,
            PlanVersion: requestedPlan.PlanVersion));

        var snoozedItem = TestDatabase.Todo("已稍后", new DateOnly(2026, 10, 23));
        var snoozed = db.SeedItem(snoozedItem, TestDatabase.Reminder(snoozedItem.Id, TestDatabase.Now.AddMinutes(-1)));
        var snoozedPlan = db.Reminders.GetActiveForItem(snoozed.Id)!;
        db.Reminders.TryAddOccurrence(new ReminderOccurrence(
            Guid.NewGuid(), snoozedPlan.Id, snoozed.Id, snoozedPlan.ReminderAtUtc,
            ReminderOccurrenceStatus.Snoozed, "tag", SnoozeOption.Defaults,
            TestDatabase.Now, "snooze:10m", TestDatabase.Now, 0, null, TestDatabase.Now, TestDatabase.Now,
            PlanVersion: snoozedPlan.PlanVersion));

        var failedItem = TestDatabase.Todo("投递失败", new DateOnly(2026, 10, 23));
        var failed = db.SeedItem(failedItem, TestDatabase.Reminder(failedItem.Id, TestDatabase.Now.AddMinutes(-1)));
        var failedPlan = db.Reminders.GetActiveForItem(failed.Id)!;
        db.Reminders.TryAddOccurrence(new ReminderOccurrence(
            Guid.NewGuid(), failedPlan.Id, failed.Id, failedPlan.ReminderAtUtc,
            ReminderOccurrenceStatus.Failed, "tag", SnoozeOption.Defaults,
            null, null, null, 1, "channel-error", TestDatabase.Now, TestDatabase.Now,
            PlanVersion: failedPlan.PlanVersion));

        var rows = query.GetOverview();
        Assert.Equal(3, rows.Count);
        Assert.Single(rows, row => ReminderQueryService.MatchesFilter(row, ReminderViewFilter.Requested));
        Assert.Single(rows, row => ReminderQueryService.MatchesFilter(row, ReminderViewFilter.Snoozed));
        Assert.Single(rows, row => ReminderQueryService.MatchesFilter(row, ReminderViewFilter.Problem));
    }
}
