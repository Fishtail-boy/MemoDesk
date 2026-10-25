using MemoDesk.Core.Reminders;

namespace MemoDesk.Core.Tests.Reminders;

public sealed class ReminderSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 6, 30, 0, TimeSpan.Zero);

    private static (ReminderScheduler Scheduler, FakeReminderRepository Repository, Guid ItemId, Guid PlanId) CreateDuePlan()
    {
        var repository = new FakeReminderRepository();
        var itemId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        repository.Plans.Add(new ReminderPlan(
            planId, itemId, Now.AddMinutes(-1), true, 1, Now, Now));
        return (new ReminderScheduler(repository), repository, itemId, planId);
    }

    private static ReminderCandidate ClaimAndHandoff(
        ReminderScheduler scheduler,
        FakeReminderRepository repository,
        string owner)
    {
        var candidate = scheduler.CollectCandidates(Now, SnoozeOption.Defaults)
            .Single(c => c.Occurrence.Status == ReminderOccurrenceStatus.Pending);
        Assert.True(scheduler.TryClaim(candidate, owner, Now));
        Assert.True(scheduler.MarkHandedOff(candidate.Occurrence.Id, owner, "test", Now));
        return candidate;
    }

    [Fact]
    public void CollectCandidates_CreatesPendingOccurrenceWithPlanVersion()
    {
        var (scheduler, repository, itemId, planId) = CreateDuePlan();

        var candidate = Assert.Single(scheduler.CollectCandidates(Now, SnoozeOption.Defaults));

        Assert.Equal(itemId, candidate.Occurrence.ItemId);
        Assert.Equal(planId, candidate.Occurrence.ReminderId);
        Assert.Equal(ReminderOccurrenceStatus.Pending, candidate.Occurrence.Status);
        Assert.Equal(1, candidate.Occurrence.PlanVersion);
        Assert.False(candidate.IsRecovery);
        Assert.Single(repository.Occurrences);
        Assert.Equal(SnoozeOption.Defaults.Count, candidate.Occurrence.OptionsSnapshot.Count);
    }

    [Fact]
    public void TryClaim_SecondScannerIsRejected()
    {
        var (scheduler, _, _, _) = CreateDuePlan();
        var candidate = scheduler.CollectCandidates(Now, SnoozeOption.Defaults).Single();

        Assert.True(scheduler.TryClaim(candidate, "owner-1", Now));
        Assert.False(scheduler.TryClaim(candidate, "owner-2", Now));

        // 非领取者不能写交接结果。
        Assert.False(scheduler.MarkHandedOff(candidate.Occurrence.Id, "owner-2", "test", Now));
        Assert.True(scheduler.MarkHandedOff(candidate.Occurrence.Id, "owner-1", "test", Now));
    }

    [Fact]
    public void AfterHandoff_NoNewOccurrenceIsCreated()
    {
        var (scheduler, repository, _, _) = CreateDuePlan();
        ClaimAndHandoff(scheduler, repository, "owner-1");

        var second = scheduler.CollectCandidates(Now, SnoozeOption.Defaults);

        Assert.Empty(second);
        Assert.Single(repository.Occurrences);
    }

    [Fact]
    public void CrashWindow_RecoversExactlyOnce()
    {
        var (scheduler, repository, _, _) = CreateDuePlan();
        var candidate = scheduler.CollectCandidates(Now, SnoozeOption.Defaults).Single();
        Assert.True(scheduler.TryClaim(candidate, "owner-1", Now));

        // 模拟进程在交接前崩溃：已领取、租约过期、尚未交接。
        repository.Occurrences[0] = repository.Occurrences[0] with
        {
            DeliveryLeaseUntilUtc = Now.AddSeconds(-1),
        };

        var recovered = Assert.Single(scheduler.CollectCandidates(Now.AddSeconds(1), SnoozeOption.Defaults));
        Assert.True(recovered.IsRecovery);
        Assert.True(scheduler.TryClaim(recovered, "owner-2", Now.AddSeconds(1)));
        Assert.True(scheduler.MarkHandedOff(recovered.Occurrence.Id, "owner-2", "inapp", Now.AddSeconds(1)));

        Assert.Empty(scheduler.CollectCandidates(Now.AddSeconds(2), SnoozeOption.Defaults));
        Assert.Equal(1, repository.Occurrences.Single().RecoveryAttempts);
        Assert.Equal(ReminderOccurrenceStatus.Requested, repository.Occurrences.Single().Status);
    }

    [Fact]
    public void Uncertain_IsNotRetriedMoreThanOnce()
    {
        var (scheduler, repository, _, _) = CreateDuePlan();
        var candidate = scheduler.CollectCandidates(Now, SnoozeOption.Defaults).Single();
        Assert.True(scheduler.TryClaim(candidate, "owner-1", Now));
        Assert.True(scheduler.MarkUncertain(candidate.Occurrence.Id, "owner-1", "timeout", Now));

        var recovered = Assert.Single(scheduler.CollectCandidates(Now.AddSeconds(1), SnoozeOption.Defaults));
        Assert.True(recovered.IsRecovery);
        Assert.True(scheduler.TryClaim(recovered, "owner-2", Now.AddSeconds(1)));
        Assert.True(scheduler.MarkUncertain(recovered.Occurrence.Id, "owner-2", "timeout", Now.AddSeconds(1)));

        Assert.Empty(scheduler.CollectCandidates(Now.AddSeconds(2), SnoozeOption.Defaults));
        Assert.Equal(1, repository.Occurrences.Single().RecoveryAttempts);
    }

    [Fact]
    public void FailedInstance_BlocksAutomaticRecreation()
    {
        var (scheduler, repository, _, _) = CreateDuePlan();
        var candidate = scheduler.CollectCandidates(Now, SnoozeOption.Defaults).Single();
        Assert.True(scheduler.TryClaim(candidate, "owner-1", Now));
        Assert.True(scheduler.MarkFailed(candidate.Occurrence.Id, "owner-1", "channel-error", Now));

        for (var round = 0; round < 5; round++)
        {
            var next = scheduler.CollectCandidates(Now.AddMinutes(round + 1), SnoozeOption.Defaults);
            Assert.Empty(next);
        }

        Assert.Single(repository.Occurrences);
    }

    [Fact]
    public void Snooze_MovesReminderAndClosesOccurrence()
    {
        var (scheduler, repository, _, _) = CreateDuePlan();
        var candidate = ClaimAndHandoff(scheduler, repository, "owner-1");

        var result = scheduler.Snooze(candidate.Occurrence.Id, "10m", TimeZoneInfo.Utc, Now);

        Assert.True(result.Success);
        Assert.Equal(Now.AddMinutes(10), result.NewReminderAtUtc);
        Assert.Equal(ReminderOccurrenceStatus.Snoozed, repository.Occurrences.Single().Status);
        Assert.Equal(Now.AddMinutes(10), repository.Plans.Single().ReminderAtUtc);
        Assert.Equal(2, repository.Plans.Single().PlanVersion);
    }

    [Fact]
    public void Snooze_SecondTimeOnSameOccurrence_Fails()
    {
        var (scheduler, repository, _, _) = CreateDuePlan();
        var candidate = ClaimAndHandoff(scheduler, repository, "owner-1");
        Assert.True(scheduler.Snooze(candidate.Occurrence.Id, "10m", TimeZoneInfo.Utc, Now).Success);

        var second = scheduler.Snooze(candidate.Occurrence.Id, "30m", TimeZoneInfo.Utc, Now);

        Assert.False(second.Success);
    }

    [Fact]
    public void EditingReminder_CancelsOldInstance_AndOldSnoozeIsRejected()
    {
        var (scheduler, repository, itemId, _) = CreateDuePlan();
        var candidate = ClaimAndHandoff(scheduler, repository, "owner-1");

        var editedAt = Now.AddHours(3);
        repository.SetReminder(itemId, editedAt, Now);

        var result = scheduler.Snooze(candidate.Occurrence.Id, "10m", TimeZoneInfo.Utc, Now);

        Assert.False(result.Success);
        Assert.Equal(ReminderOccurrenceStatus.Cancelled, repository.Occurrences.Single().Status);
        Assert.Equal(editedAt, repository.Plans.Single().ReminderAtUtc);
        Assert.Equal(2, repository.Plans.Single().PlanVersion);
    }

    [Fact]
    public void CancellingReminder_OldSnoozeDoesNotReenablePlan()
    {
        var (scheduler, repository, itemId, _) = CreateDuePlan();
        var candidate = ClaimAndHandoff(scheduler, repository, "owner-1");

        repository.SetReminder(itemId, null, Now);
        var result = scheduler.Snooze(candidate.Occurrence.Id, "10m", TimeZoneInfo.Utc, Now);

        Assert.False(result.Success);
        Assert.Null(repository.GetActiveForItem(itemId));
        Assert.Equal(ReminderOccurrenceStatus.Cancelled, repository.Occurrences.Single().Status);
    }

    [Fact]
    public void IsCatchUp_TrueOnlyWhenLateMoreThanTwoMinutes()
    {
        var occurrence = new ReminderOccurrence(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now, ReminderOccurrenceStatus.Pending,
            "tag", SnoozeOption.Defaults, null, null, null, 0, null, Now, Now);

        Assert.False(ReminderScheduler.IsCatchUp(occurrence, Now.AddMinutes(1)));
        Assert.True(ReminderScheduler.IsCatchUp(occurrence, Now.AddMinutes(3)));
    }
}
