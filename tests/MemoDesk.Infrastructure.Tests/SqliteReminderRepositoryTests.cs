using MemoDesk.Core.Domain;
using MemoDesk.Core.Reminders;

namespace MemoDesk.Infrastructure.Tests;

public sealed class SqliteReminderRepositoryTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    [Fact]
    public void DuePlanAndPendingOccurrence_AreQueried()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("提醒我", new DateOnly(2026, 10, 21));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddMinutes(-1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;

        var duePlans = db.Reminders.GetActiveDue(TestDatabase.Now);
        Assert.Contains(duePlans, p => p.Id == plan.Id);

        var occurrence = CreateOccurrence(plan, seeded.Id, ReminderOccurrenceStatus.Pending);
        db.Reminders.TryAddOccurrence(occurrence);

        Assert.Contains(db.Reminders.GetPendingDue(TestDatabase.Now), o => o.Id == occurrence.Id);
        Assert.NotNull(db.Reminders.GetBlockingOccurrenceForPlan(plan.Id, plan.PlanVersion));
        Assert.Equal(SnoozeOption.Defaults.Count, db.Reminders.GetOccurrence(occurrence.Id)!.OptionsSnapshot.Count);
    }

    [Fact]
    public void Claim_IsAtomic_AndHandoffMovesOutOfPending()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("提醒我", new DateOnly(2026, 10, 21));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddMinutes(-1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;
        var occurrence = CreateOccurrence(plan, seeded.Id, ReminderOccurrenceStatus.Pending);
        db.Reminders.TryAddOccurrence(occurrence);

        Assert.True(db.Reminders.TryClaimDelivery(occurrence.Id, "owner-1", TestDatabase.Now, Lease));
        Assert.False(db.Reminders.TryClaimDelivery(occurrence.Id, "owner-2", TestDatabase.Now, Lease));

        // 非领取者不能写结果。
        Assert.False(db.Reminders.MarkHandedOff(occurrence.Id, "owner-2", "test", TestDatabase.Now));
        Assert.True(db.Reminders.MarkHandedOff(occurrence.Id, "owner-1", "inapp", TestDatabase.Now.AddSeconds(1)));

        var reloaded = db.Reminders.GetOccurrence(occurrence.Id)!;
        Assert.Equal(ReminderOccurrenceStatus.Requested, reloaded.Status);
        Assert.Equal("inapp", reloaded.Channel);
        Assert.NotNull(reloaded.HandedOffAtUtc);
        Assert.Empty(db.Reminders.GetPendingDue(TestDatabase.Now.AddMinutes(1)));
        Assert.NotNull(db.Reminders.GetBlockingOccurrenceForPlan(plan.Id, plan.PlanVersion));
    }

    [Fact]
    public void Recovery_IsLimitedToOneAttempt()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("崩溃恢复", new DateOnly(2026, 10, 21));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddMinutes(-1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;

        // 模拟领取后崩溃：已尝试一次、租约为空、仍未交接。
        var occurrence = CreateOccurrence(plan, seeded.Id, ReminderOccurrenceStatus.Pending) with
        {
            DeliveryAttempts = 1,
        };
        db.Reminders.TryAddOccurrence(occurrence);

        var recoverable = Assert.Single(db.Reminders.GetRecoverable(TestDatabase.Now.AddMinutes(1)));
        Assert.True(db.Reminders.TryClaimRecovery(occurrence.Id, "owner-r", TestDatabase.Now.AddMinutes(1), Lease));
        Assert.True(db.Reminders.MarkHandedOff(occurrence.Id, "owner-r", "inapp", TestDatabase.Now.AddMinutes(1)));

        Assert.Empty(db.Reminders.GetRecoverable(TestDatabase.Now.AddMinutes(2)));
        Assert.Equal(1, db.Reminders.GetOccurrence(occurrence.Id)!.RecoveryAttempts);
    }

    [Fact]
    public void Snooze_UpdatesOccurrenceAndPlanAtomically()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("稍后提醒", new DateOnly(2026, 10, 21));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddMinutes(-1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;
        var occurrence = CreateOccurrence(plan, seeded.Id, ReminderOccurrenceStatus.Requested);
        db.Reminders.TryAddOccurrence(occurrence);

        var newTime = TestDatabase.Now.AddMinutes(10);
        Assert.True(db.Reminders.SnoozeOccurrence(occurrence.Id, newTime, "10m", TestDatabase.Now));

        var reloadedOccurrence = db.Reminders.GetOccurrence(occurrence.Id)!;
        Assert.Equal(ReminderOccurrenceStatus.Snoozed, reloadedOccurrence.Status);
        Assert.Equal("snooze:10m", reloadedOccurrence.Action);

        var reloadedPlan = db.Reminders.GetActiveForItem(seeded.Id)!;
        Assert.Equal(newTime, reloadedPlan.ReminderAtUtc);
        Assert.Equal(2, reloadedPlan.PlanVersion);

        Assert.False(db.Reminders.SnoozeOccurrence(occurrence.Id, newTime.AddMinutes(10), "30m", TestDatabase.Now));
    }

    [Fact]
    public void Snooze_StalePlanVersion_IsRejected()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("旧版本", new DateOnly(2026, 10, 21));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddMinutes(-1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;
        var occurrence = CreateOccurrence(plan, seeded.Id, ReminderOccurrenceStatus.Requested) with
        {
            PlanVersion = plan.PlanVersion - 1,
        };
        db.Reminders.TryAddOccurrence(occurrence);

        Assert.False(db.Reminders.SnoozeOccurrence(
            occurrence.Id, TestDatabase.Now.AddMinutes(10), "10m", TestDatabase.Now));
        Assert.Equal(
            ReminderOccurrenceStatus.Requested,
            db.Reminders.GetOccurrence(occurrence.Id)!.Status);
    }

    [Fact]
    public void SetReminder_CancelsOpenInstance_AndOldActionCannotOverride()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("改计划", new DateOnly(2026, 10, 21));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddMinutes(-1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;
        var occurrence = CreateOccurrence(plan, seeded.Id, ReminderOccurrenceStatus.Requested);
        db.Reminders.TryAddOccurrence(occurrence);

        var editedAt = TestDatabase.Now.AddHours(3);
        db.Reminders.SetReminder(seeded.Id, editedAt, TestDatabase.Now);

        Assert.Equal(
            ReminderOccurrenceStatus.Cancelled,
            db.Reminders.GetOccurrence(occurrence.Id)!.Status);
        Assert.Equal("reminder-changed", db.Reminders.GetOccurrence(occurrence.Id)!.CancelReason);

        var updatedPlan = db.Reminders.GetActiveForItem(seeded.Id)!;
        Assert.Equal(editedAt, updatedPlan.ReminderAtUtc);
        Assert.Equal(plan.PlanVersion + 1, updatedPlan.PlanVersion);

        Assert.False(db.Reminders.SnoozeOccurrence(
            occurrence.Id, TestDatabase.Now.AddMinutes(10), "10m", TestDatabase.Now));
        Assert.Equal(editedAt, db.Reminders.GetActiveForItem(seeded.Id)!.ReminderAtUtc);
    }

    [Fact]
    public void SetReminder_Null_DisablesAndCancels()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Memo("取消提醒", new DateOnly(2026, 10, 21));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddMinutes(-1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;
        var occurrence = CreateOccurrence(plan, seeded.Id, ReminderOccurrenceStatus.Requested);
        db.Reminders.TryAddOccurrence(occurrence);

        db.Reminders.SetReminder(seeded.Id, null, TestDatabase.Now);

        Assert.Null(db.Reminders.GetActiveForItem(seeded.Id));
        Assert.Equal(
            ReminderOccurrenceStatus.Cancelled,
            db.Reminders.GetOccurrence(occurrence.Id)!.Status);
    }

    [Fact]
    public void TryAddOccurrence_IsUniquePerPlanVersion()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("唯一实例", new DateOnly(2026, 10, 21));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddMinutes(-1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;

        Assert.True(db.Reminders.TryAddOccurrence(CreateOccurrence(plan, seeded.Id, ReminderOccurrenceStatus.Pending)));
        Assert.False(db.Reminders.TryAddOccurrence(CreateOccurrence(plan, seeded.Id, ReminderOccurrenceStatus.Pending)));
        Assert.Single(db.Reminders.GetPendingDue(TestDatabase.Now));
    }

    [Fact]
    public void ManualReset_AllowsFailedAndRecoveryExhaustedPending_ButRejectsInFlightAndFreshPending()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("人工重发", new DateOnly(2026, 10, 21));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddMinutes(-1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;

        var failed = CreateOccurrence(plan, seeded.Id, ReminderOccurrenceStatus.Failed) with
        {
            DeliveryAttempts = 1,
            RecoveryAttempts = 1,
        };
        db.Reminders.TryAddOccurrence(failed);

        Assert.Equal(ManualRetryOutcome.Reset, db.Reminders.TryResetOccurrenceForManualDelivery(failed.Id, TestDatabase.Now));
        var reset = db.Reminders.GetOccurrence(failed.Id)!;
        Assert.Equal(ReminderOccurrenceStatus.Pending, reset.Status);
        Assert.Equal(1, reset.ManualAttempts);
        Assert.True(reset.RecoveryAttempts >= 1);

        // 刚重置（attempts=0）不能被第二次点击再次重置。
        Assert.Equal(
            ManualRetryOutcome.NotEligible,
            db.Reminders.TryResetOccurrenceForManualDelivery(failed.Id, TestDatabase.Now));

        // 人工重发领取后崩溃：Pending + attempts=1 + recovery>=1 + 租约过期 → 可再次人工重发。
        db.Reminders.SetReminder(seeded.Id, TestDatabase.Now.AddHours(1), TestDatabase.Now);
        var nextPlan = db.Reminders.GetActiveForItem(seeded.Id)!;
        var crashed = CreateOccurrence(nextPlan, seeded.Id, ReminderOccurrenceStatus.Pending) with
        {
            DeliveryAttempts = 1,
            RecoveryAttempts = 1,
            DeliveryLeaseUntilUtc = TestDatabase.Now.AddMinutes(-1),
        };
        db.Reminders.TryAddOccurrence(crashed);
        Assert.Equal(
            ManualRetryOutcome.Reset,
            db.Reminders.TryResetOccurrenceForManualDelivery(crashed.Id, TestDatabase.Now));
    }

    [Fact]
    public void ManualReset_RejectsInFlightPlanChangedAndCompletedItem()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("拒绝条件", new DateOnly(2026, 10, 21));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddMinutes(-1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;

        var inFlight = CreateOccurrence(plan, seeded.Id, ReminderOccurrenceStatus.Failed) with
        {
            DeliveryLeaseUntilUtc = TestDatabase.Now.AddMinutes(5),
        };
        db.Reminders.TryAddOccurrence(inFlight);
        Assert.Equal(
            ManualRetryOutcome.InFlight,
            db.Reminders.TryResetOccurrenceForManualDelivery(inFlight.Id, TestDatabase.Now));

        var stale = CreateOccurrence(plan, seeded.Id, ReminderOccurrenceStatus.Failed, plan.PlanVersion + 1);
        db.Reminders.TryAddOccurrence(stale);
        Assert.Equal(
            ManualRetryOutcome.PlanChanged,
            db.Reminders.TryResetOccurrenceForManualDelivery(stale.Id, TestDatabase.Now));

        // 计划仍启用但事项被人为改成 Completed：应拒绝并报告事项不可用。
        using (var connection = db.Factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE items SET status = 'Completed' WHERE id = $id;
                UPDATE reminder_occurrences SET delivery_lease_until_utc = NULL WHERE id = $occurrenceId;
                """;
            command.Parameters.AddWithValue("$id", seeded.Id.ToString("N"));
            command.Parameters.AddWithValue("$occurrenceId", inFlight.Id.ToString("N"));
            command.ExecuteNonQuery();
        }

        Assert.Equal(
            ManualRetryOutcome.ItemNotOpen,
            db.Reminders.TryResetOccurrenceForManualDelivery(inFlight.Id, TestDatabase.Now));
    }

    [Fact]
    public void ScheduleRegistration_WriteBackIsVersionConditional_AndOldTagGetsIndependentCancellation()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("计划通知", new DateOnly(2026, 10, 23));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddHours(1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;
        var tag = $"memo-{seeded.Id:N}-{plan.PlanVersion}";

        var occurrenceId = db.Reminders.BeginScheduleRegistration(
            plan.Id, plan.PlanVersion, plan.ReminderAtUtc, tag, SnoozeOption.Defaults, TestDatabase.Now);
        Assert.NotNull(occurrenceId);
        var registering = db.Reminders.GetReminder(plan.Id)!;
        Assert.Equal("registering", registering.ScheduleState);
        Assert.Equal("windows-pending", registering.DeliveryOwner);

        Assert.True(db.Reminders.CompleteScheduleRegistration(
            plan.Id, plan.PlanVersion, plan.ReminderAtUtc, true, null, TestDatabase.Now));
        var scheduled = db.Reminders.GetReminder(plan.Id)!;
        Assert.Equal("scheduled", scheduled.ScheduleState);
        Assert.Equal("windows", scheduled.DeliveryOwner);

        // 修改提醒：版本递增，旧 tag 进入独立撤销任务，登记字段清空。
        var editedAt = TestDatabase.Now.AddHours(3);
        db.Reminders.SetReminder(seeded.Id, editedAt, TestDatabase.Now);

        var edited = db.Reminders.GetActiveForItem(seeded.Id)!;
        Assert.Equal(editedAt, edited.ReminderAtUtc);
        Assert.Equal(plan.PlanVersion + 1, edited.PlanVersion);
        Assert.Null(edited.ScheduledTag);
        Assert.Null(edited.ScheduleState);
        Assert.Null(edited.DeliveryOwner);

        var cancellation = Assert.Single(db.Reminders.GetPendingCancellations(10));
        Assert.Equal(tag, cancellation.Tag);
        Assert.Equal(plan.PlanVersion, cancellation.PlanVersion);
        Assert.Equal("reminder-changed", cancellation.Reason);

        // 旧登记结果不能再写回新计划。
        Assert.False(db.Reminders.CompleteScheduleRegistration(
            plan.Id, plan.PlanVersion, plan.ReminderAtUtc, true, null, TestDatabase.Now));

        db.Reminders.MarkCancellationResult(cancellation.Id, true, null, TestDatabase.Now);
        Assert.Empty(db.Reminders.GetPendingCancellations(10));
    }

    [Fact]
    public void CompletingItem_EnqueuesCancellationForScheduledPlan()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("完成计划通知", new DateOnly(2026, 10, 23));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddHours(1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;
        var tag = $"memo-{seeded.Id:N}-{plan.PlanVersion}";

        Assert.NotNull(db.Reminders.BeginScheduleRegistration(
            plan.Id, plan.PlanVersion, plan.ReminderAtUtc, tag, SnoozeOption.Defaults, TestDatabase.Now));
        Assert.True(db.Reminders.CompleteScheduleRegistration(
            plan.Id, plan.PlanVersion, plan.ReminderAtUtc, true, null, TestDatabase.Now));

        Assert.True(db.Items.TrySetStatus(seeded.Id, ItemStatus.Completed, TestDatabase.Now));

        var cancellation = Assert.Single(db.Reminders.GetPendingCancellations(10));
        Assert.Equal(tag, cancellation.Tag);
        Assert.Equal("item-completed", cancellation.Reason);
        Assert.Null(db.Reminders.GetActiveForItem(seeded.Id));
    }

    [Fact]
    public void ScheduleRevocation_KeepsWindowsOwnershipUntilCancelSucceeds()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("撤销所有权", new DateOnly(2026, 10, 23));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddHours(1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;
        var tag = $"memo-{seeded.Id:N}-{plan.PlanVersion}";

        Assert.NotNull(db.Reminders.BeginScheduleRegistration(
            plan.Id, plan.PlanVersion, plan.ReminderAtUtc, tag, SnoozeOption.Defaults, TestDatabase.Now));
        Assert.True(db.Reminders.CompleteScheduleRegistration(
            plan.Id, plan.PlanVersion, plan.ReminderAtUtc, true, null, TestDatabase.Now));

        Assert.True(db.Reminders.BeginScheduleRevocation(plan.Id, "app-exit", TestDatabase.Now));
        var revoking = db.Reminders.GetReminder(plan.Id)!;
        Assert.Equal("revoking", revoking.ScheduleState);
        Assert.Equal("windows", revoking.DeliveryOwner);
        Assert.Equal(tag, revoking.ScheduledTag);
        Assert.Single(db.Reminders.GetPendingCancellations(10));

        // 撤销失败：保持 windows/uncertain，展示端不得接管投递。
        Assert.True(db.Reminders.MarkScheduleUncertain(plan.Id, tag, "cancel-failed", TestDatabase.Now));
        var uncertain = db.Reminders.GetReminder(plan.Id)!;
        Assert.Equal("uncertain", uncertain.ScheduleState);
        Assert.Equal("windows", uncertain.DeliveryOwner);

        // 旧 tag 或错误状态不能交回所有权；撤销成功后才变为 app。
        Assert.False(db.Reminders.CompleteScheduleRevocation(plan.Id, "stale-tag", TestDatabase.Now));
        Assert.Equal("windows", db.Reminders.GetReminder(plan.Id)!.DeliveryOwner);
        Assert.True(db.Reminders.CompleteScheduleRevocation(plan.Id, tag, TestDatabase.Now));

        var handedBack = db.Reminders.GetReminder(plan.Id)!;
        Assert.Null(handedBack.ScheduleState);
        Assert.Null(handedBack.ScheduledTag);
        Assert.Equal("app", handedBack.DeliveryOwner);
    }

    [Fact]
    public void MarkScheduledDeliveryUnknown_IsAtomic_AndRollsBackWhenOccurrenceDoesNotMatch()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("投递未知", new DateOnly(2026, 10, 23));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddHours(1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;
        var tag = $"memo-{seeded.Id:N}-{plan.PlanVersion}";

        var occurrenceId = db.Reminders.BeginScheduleRegistration(
            plan.Id, plan.PlanVersion, plan.ReminderAtUtc, tag, SnoozeOption.Defaults, TestDatabase.Now);
        Assert.NotNull(occurrenceId);
        Assert.True(db.Reminders.CompleteScheduleRegistration(
            plan.Id, plan.PlanVersion, plan.ReminderAtUtc, true, null, TestDatabase.Now));

        // 实例不匹配：计划更新必须随实例更新一起回滚。
        Assert.False(db.Reminders.MarkScheduledDeliveryUnknown(
            plan.Id, plan.PlanVersion, Guid.NewGuid(), TestDatabase.Now));
        var unchanged = db.Reminders.GetReminder(plan.Id)!;
        Assert.Equal("scheduled", unchanged.ScheduleState);
        Assert.Equal("windows", unchanged.DeliveryOwner);
        Assert.Equal(
            ReminderOccurrenceStatus.Pending,
            db.Reminders.GetOccurrence(occurrenceId.Value)!.Status);

        Assert.True(db.Reminders.MarkScheduledDeliveryUnknown(
            plan.Id, plan.PlanVersion, occurrenceId.Value, TestDatabase.Now));

        var failedPlan = db.Reminders.GetReminder(plan.Id)!;
        Assert.Equal("failed", failedPlan.ScheduleState);
        Assert.Equal("app", failedPlan.DeliveryOwner);

        var unknown = db.Reminders.GetOccurrence(occurrenceId.Value)!;
        Assert.Equal(ReminderOccurrenceStatus.Uncertain, unknown.Status);
        Assert.True(unknown.DeliveryAttempts >= 1);
        Assert.Null(unknown.DeliveryLeaseUntilUtc);
    }

    [Fact]
    public void Snooze_ScheduledPlan_ClearsRegistrationAndEnqueuesOldTagCancellation()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("系统通知稍后", new DateOnly(2026, 10, 23));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddHours(1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;
        var tag = $"memo-{seeded.Id:N}-{plan.PlanVersion}";

        var occurrenceId = db.Reminders.BeginScheduleRegistration(
            plan.Id, plan.PlanVersion, plan.ReminderAtUtc, tag, SnoozeOption.Defaults, TestDatabase.Now);
        Assert.NotNull(occurrenceId);
        Assert.True(db.Reminders.CompleteScheduleRegistration(
            plan.Id, plan.PlanVersion, plan.ReminderAtUtc, true, null, TestDatabase.Now));

        var next = TestDatabase.Now.AddMinutes(10);
        Assert.True(db.Reminders.SnoozeOccurrence(occurrenceId.Value, next, "10m", TestDatabase.Now));

        var snoozedPlan = db.Reminders.GetActiveForItem(seeded.Id)!;
        Assert.Equal(next, snoozedPlan.ReminderAtUtc);
        Assert.Equal(plan.PlanVersion + 1, snoozedPlan.PlanVersion);
        Assert.Null(snoozedPlan.ScheduledTag);
        Assert.Null(snoozedPlan.ScheduleState);
        Assert.Null(snoozedPlan.DeliveryOwner);

        var cancellation = Assert.Single(db.Reminders.GetPendingCancellations(10));
        Assert.Equal(tag, cancellation.Tag);
        Assert.Equal("snoozed", cancellation.Reason);
        Assert.Equal(plan.PlanVersion, cancellation.PlanVersion);
    }

    [Fact]
    public void ScheduleReregistration_ReusesOccurrenceOptionSnapshot()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("重登记选项", new DateOnly(2026, 10, 23));
        var seeded = db.SeedItem(item, TestDatabase.Reminder(item.Id, TestDatabase.Now.AddHours(1)));
        var plan = db.Reminders.GetActiveForItem(seeded.Id)!;
        var tag = $"memo-{seeded.Id:N}-{plan.PlanVersion}";
        var originalOptions = new[]
        {
            new SnoozeOption("original-15", "15 分钟", SnoozeKind.Relative, 15, null),
        };

        var first = db.Reminders.BeginScheduleRegistration(
            plan.Id, plan.PlanVersion, plan.ReminderAtUtc, tag, originalOptions, TestDatabase.Now);
        Assert.NotNull(first);
        Assert.True(db.Reminders.CompleteScheduleRegistration(
            plan.Id, plan.PlanVersion, plan.ReminderAtUtc, false, "synthetic-failure", TestDatabase.Now));

        // 设置已改成新选项 ID；同一计划版本重登记必须沿用实例快照，按钮才能被旧实例识别。
        var changedOptions = new[]
        {
            new SnoozeOption("changed-30", "30 分钟", SnoozeKind.Relative, 30, null),
        };
        var second = db.Reminders.BeginScheduleRegistration(
            plan.Id, plan.PlanVersion, plan.ReminderAtUtc, tag, changedOptions, TestDatabase.Now);

        Assert.Equal(first, second);
        Assert.NotNull(db.Reminders.GetBlockingOccurrenceForPlan(plan.Id, plan.PlanVersion));
        var snapshot = db.Reminders.GetOccurrence(first!.Value)!.OptionsSnapshot;
        Assert.Equal("original-15", Assert.Single(snapshot).Id);
    }

    private static ReminderOccurrence CreateOccurrence(
        ReminderPlan plan,
        Guid itemId,
        ReminderOccurrenceStatus status,
        int planVersion = -1) => new(
        Guid.NewGuid(),
        plan.Id,
        itemId,
        plan.ReminderAtUtc,
        status,
        "tag",
        SnoozeOption.Defaults,
        status == ReminderOccurrenceStatus.Requested ? TestDatabase.Now : null,
        null,
        null,
        0,
        null,
        TestDatabase.Now,
        TestDatabase.Now,
        PlanVersion: planVersion < 0 ? plan.PlanVersion : planVersion);
}
