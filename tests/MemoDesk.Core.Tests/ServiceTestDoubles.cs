using MemoDesk.Core.Capturing;
using MemoDesk.Core.Drafting;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Core.Parsing;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Timeline;

namespace MemoDesk.Core.Tests;

internal sealed class RecordingItemStore : IItemStore
{
    public DraftConfirmationOutcome Result { get; set; } = DraftConfirmationOutcome.Confirmed;

    public List<Item> Items { get; } = new();

    public List<ReminderPlan> Reminders { get; } = new();

    public List<TimelineEvent> Events { get; } = new();

    public Item? GetById(Guid id) => Items.FirstOrDefault(item => item.Id == id);

    public void Update(Item item)
    {
    }

    public MemoEditSaveResult UpdateWithReminder(
        Item item,
        ReminderEditIntent reminder,
        DateTimeOffset now) => MemoEditSaveResult.Success();

    public bool TrySetStatus(Guid itemId, ItemStatus status, DateTimeOffset now, Guid? reminderOccurrenceId = null) => true;

    public DraftConfirmationOutcome ConfirmDraft(
        Guid draftId,
        int expectedDraftVersion,
        IReadOnlyList<Item> items,
        IReadOnlyList<ReminderPlan> reminders,
        IReadOnlyList<TimelineEvent> events,
        DateTimeOffset now,
        int expectedEditVersion = 0)
    {
        if (Result != DraftConfirmationOutcome.Confirmed)
        {
            return Result;
        }

        Items.AddRange(items);
        Reminders.AddRange(reminders);
        Events.AddRange(events);
        return DraftConfirmationOutcome.Confirmed;
    }
}

internal sealed class FakeReminderRepository : IReminderRepository
{
    public List<ReminderPlan> Plans { get; } = new();

    public List<ReminderOccurrence> Occurrences { get; } = new();

    public IReadOnlyList<ReminderPlan> GetActiveDue(DateTimeOffset atUtc) =>
        Plans.Where(plan => plan.Enabled && plan.ReminderAtUtc <= atUtc).ToArray();

    public ReminderPlan? GetActiveForItem(Guid itemId) =>
        Plans.FirstOrDefault(plan => plan.ItemId == itemId && plan.Enabled);

    public ReminderPlan? GetReminder(Guid reminderId) =>
        Plans.FirstOrDefault(plan => plan.Id == reminderId);

    public IReadOnlyList<ReminderPlan> GetActiveBetween(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        Plans.Where(plan => plan.Enabled
            && plan.ReminderAtUtc >= fromUtc
            && plan.ReminderAtUtc <= toUtc).ToArray();

    public List<NotificationCancellation> Cancellations { get; } = new();

    public Guid? BeginScheduleRegistration(
        Guid reminderId,
        int planVersion,
        DateTimeOffset scheduledAtUtc,
        string notificationTag,
        IReadOnlyList<SnoozeOption> optionsSnapshot,
        DateTimeOffset now)
    {
        var index = Plans.FindIndex(plan => plan.Id == reminderId
            && plan.Enabled
            && plan.PlanVersion == planVersion
            && plan.ReminderAtUtc == scheduledAtUtc);
        if (index < 0)
        {
            return null;
        }

        var occurrence = Occurrences.FirstOrDefault(o => o.ReminderId == reminderId
            && o.PlanVersion == planVersion
            && o.PlanVersion > 0);
        if (occurrence is null)
        {
            occurrence = new ReminderOccurrence(
                Guid.NewGuid(), reminderId, Plans[index].ItemId, scheduledAtUtc,
                ReminderOccurrenceStatus.Pending, notificationTag, optionsSnapshot,
                null, null, null, 0, null, now, now, PlanVersion: planVersion);
            Occurrences.Add(occurrence);
        }

        Plans[index] = Plans[index] with
        {
            DeliveryOwner = "windows-pending",
            ScheduledTag = notificationTag,
            ScheduledAtUtc = scheduledAtUtc,
            ScheduleState = "registering",
            SchedulePlanVersion = planVersion,
            ScheduleUpdatedAtUtc = now,
        };
        return occurrence.Id;
    }

    public bool CompleteScheduleRegistration(
        Guid reminderId,
        int planVersion,
        DateTimeOffset scheduledAtUtc,
        bool success,
        string? error,
        DateTimeOffset now)
    {
        var index = Plans.FindIndex(plan => plan.Id == reminderId
            && plan.Enabled
            && plan.PlanVersion == planVersion
            && plan.ReminderAtUtc == scheduledAtUtc);
        if (index < 0)
        {
            return false;
        }

        Plans[index] = Plans[index] with
        {
            ScheduleState = success ? "scheduled" : "failed",
            DeliveryOwner = success ? "windows" : "app",
            ScheduleError = error,
            ScheduleUpdatedAtUtc = now,
        };
        return true;
    }

    public void EnqueueCancellation(Guid reminderId, int planVersion, string notificationTag, string reason, DateTimeOffset now)
    {
        if (Cancellations.Any(c => c.Tag == notificationTag && c.State != "cancelled"))
        {
            return;
        }

        Cancellations.Add(new NotificationCancellation(
            Guid.NewGuid(), reminderId, planVersion, notificationTag, reason, "pending", 0, null));
    }

    public IReadOnlyList<NotificationCancellation> GetPendingCancellations(int limit) =>
        Cancellations.Where(c => c.State is "pending" or "failed").Take(limit).ToArray();

    public void MarkCancellationResult(Guid cancellationId, bool success, string? error, DateTimeOffset now)
    {
        var index = Cancellations.FindIndex(c => c.Id == cancellationId);
        if (index >= 0)
        {
            Cancellations[index] = Cancellations[index] with
            {
                State = success ? "cancelled" : "failed",
                Attempts = Cancellations[index].Attempts + 1,
                LastError = error,
            };
        }
    }

    public ReminderOccurrence? GetOccurrence(Guid occurrenceId) =>
        Occurrences.FirstOrDefault(o => o.Id == occurrenceId);

    public ReminderOccurrence? GetBlockingOccurrenceForPlan(Guid reminderId, int planVersion) =>
        Occurrences.FirstOrDefault(o => o.ReminderId == reminderId
            && o.PlanVersion == planVersion
            && o.Status is ReminderOccurrenceStatus.Pending
                or ReminderOccurrenceStatus.Requested
                or ReminderOccurrenceStatus.Uncertain
                or ReminderOccurrenceStatus.Failed);

    public IReadOnlyList<ReminderOccurrence> GetPendingDue(DateTimeOffset atUtc) =>
        Occurrences.Where(o => o.Status == ReminderOccurrenceStatus.Pending && o.ScheduledAtUtc <= atUtc).ToArray();

    public IReadOnlyList<ReminderOccurrence> GetRecoverable(DateTimeOffset atUtc) =>
        Occurrences.Where(o => o.ScheduledAtUtc <= atUtc
            && (o.DeliveryLeaseUntilUtc is null || o.DeliveryLeaseUntilUtc <= atUtc)
            && ((o.Status == ReminderOccurrenceStatus.Pending && o.DeliveryAttempts >= 1 && o.RecoveryAttempts < 1)
                || (o.Status == ReminderOccurrenceStatus.Uncertain && o.RecoveryAttempts < 1))).ToArray();

    public bool TryAddOccurrence(ReminderOccurrence occurrence)
    {
        if (occurrence.PlanVersion > 0
            && Occurrences.Any(o => o.ReminderId == occurrence.ReminderId
                && o.PlanVersion == occurrence.PlanVersion))
        {
            return false;
        }

        Occurrences.Add(occurrence);
        return true;
    }

    public bool TryClaimDelivery(Guid occurrenceId, string owner, DateTimeOffset now, TimeSpan leaseDuration)
    {
        var index = Occurrences.FindIndex(o => o.Id == occurrenceId
            && o.Status == ReminderOccurrenceStatus.Pending
            && o.DeliveryAttempts == 0
            && o.ScheduledAtUtc <= now
            && (o.DeliveryLeaseUntilUtc is null || o.DeliveryLeaseUntilUtc <= now));
        if (index < 0)
        {
            return false;
        }

        Occurrences[index] = Occurrences[index] with
        {
            DeliveryAttempts = Occurrences[index].DeliveryAttempts + 1,
            DeliveryOwner = owner,
            DeliveryLeaseUntilUtc = now + leaseDuration,
            UpdatedAtUtc = now,
        };
        return true;
    }

    public bool TryClaimRecovery(Guid occurrenceId, string owner, DateTimeOffset now, TimeSpan leaseDuration)
    {
        var index = Occurrences.FindIndex(o => o.Id == occurrenceId
            && o.RecoveryAttempts < 1
            && (o.DeliveryLeaseUntilUtc is null || o.DeliveryLeaseUntilUtc <= now)
            && (o.Status == ReminderOccurrenceStatus.Uncertain
                || (o.Status == ReminderOccurrenceStatus.Pending && o.DeliveryAttempts >= 1)));
        if (index < 0)
        {
            return false;
        }

        Occurrences[index] = Occurrences[index] with
        {
            Status = ReminderOccurrenceStatus.Uncertain,
            RecoveryAttempts = Occurrences[index].RecoveryAttempts + 1,
            DeliveryOwner = owner,
            DeliveryLeaseUntilUtc = now + leaseDuration,
            UpdatedAtUtc = now,
        };
        return true;
    }

    public bool MarkHandedOff(Guid occurrenceId, string owner, string channel, DateTimeOffset now)
    {
        var index = Occurrences.FindIndex(o => o.Id == occurrenceId
            && o.DeliveryOwner == owner
            && o.Status is ReminderOccurrenceStatus.Pending or ReminderOccurrenceStatus.Uncertain);
        if (index < 0)
        {
            return false;
        }

        var occurrence = Occurrences[index];
        Occurrences[index] = occurrence with
        {
            Status = ReminderOccurrenceStatus.Requested,
            HandedOffAtUtc = occurrence.HandedOffAtUtc ?? now,
            RequestedAtUtc = occurrence.RequestedAtUtc ?? now,
            Channel = channel,
            DeliveryOwner = null,
            DeliveryLeaseUntilUtc = null,
            UpdatedAtUtc = now,
        };
        return true;
    }

    public bool MarkDeliveryFailed(Guid occurrenceId, string owner, string? error, DateTimeOffset now) =>
        UpdateDeliveryResult(occurrenceId, owner, ReminderOccurrenceStatus.Failed, error, now);

    public bool MarkDeliveryUncertain(Guid occurrenceId, string owner, string? error, DateTimeOffset now) =>
        UpdateDeliveryResult(occurrenceId, owner, ReminderOccurrenceStatus.Uncertain, error, now);

    public bool MarkOccurrenceStatus(
        Guid occurrenceId,
        ReminderOccurrenceStatus status,
        string? action,
        DateTimeOffset atUtc)
    {
        var index = Occurrences.FindIndex(o => o.Id == occurrenceId
            && o.Status is ReminderOccurrenceStatus.Pending
                or ReminderOccurrenceStatus.Requested
                or ReminderOccurrenceStatus.Uncertain);
        if (index < 0)
        {
            return false;
        }

        Occurrences[index] = Occurrences[index] with
        {
            Status = status,
            Action = action,
            ActionAtUtc = atUtc,
            DeliveryOwner = null,
            DeliveryLeaseUntilUtc = null,
            UpdatedAtUtc = atUtc,
        };
        return true;
    }

    public void SetReminder(Guid itemId, DateTimeOffset? reminderAtUtc, DateTimeOffset now)
    {
        for (var i = 0; i < Occurrences.Count; i++)
        {
            if (Occurrences[i].ItemId == itemId
                && Occurrences[i].Status is ReminderOccurrenceStatus.Pending
                    or ReminderOccurrenceStatus.Requested
                    or ReminderOccurrenceStatus.Uncertain)
            {
                Occurrences[i] = Occurrences[i] with
                {
                    Status = ReminderOccurrenceStatus.Cancelled,
                    CancelReason = "reminder-changed",
                    DeliveryOwner = null,
                    DeliveryLeaseUntilUtc = null,
                    UpdatedAtUtc = now,
                };
            }
        }

        var active = Plans.FirstOrDefault(p => p.ItemId == itemId && p.Enabled);
        if (reminderAtUtc is null)
        {
            if (active is not null)
            {
                var index = Plans.IndexOf(active);
                Plans[index] = active with { Enabled = false, PlanVersion = active.PlanVersion + 1, UpdatedAtUtc = now };
            }

            return;
        }

        if (active is not null)
        {
            var index = Plans.IndexOf(active);
            Plans[index] = active with
            {
                ReminderAtUtc = reminderAtUtc.Value,
                PlanVersion = active.PlanVersion + 1,
                UpdatedAtUtc = now,
            };
            return;
        }

        Plans.Add(new ReminderPlan(Guid.NewGuid(), itemId, reminderAtUtc.Value, true, 1, now, now));
    }

    public ManualRetryOutcome TryResetOccurrenceForManualDelivery(Guid occurrenceId, DateTimeOffset now)
    {
        var index = Occurrences.FindIndex(o => o.Id == occurrenceId);
        if (index < 0)
        {
            return ManualRetryOutcome.NotEligible;
        }

        var occurrence = Occurrences[index];
        if (occurrence.DeliveryLeaseUntilUtc is { } lease && lease > now)
        {
            return ManualRetryOutcome.InFlight;
        }

        var eligible = occurrence.Status == ReminderOccurrenceStatus.Failed
            || occurrence.Status == ReminderOccurrenceStatus.Uncertain
            || (occurrence.Status == ReminderOccurrenceStatus.Pending
                && occurrence.DeliveryAttempts >= 1
                && occurrence.RecoveryAttempts >= 1);
        if (!eligible)
        {
            return ManualRetryOutcome.NotEligible;
        }

        var plan = Plans.FirstOrDefault(p => p.Id == occurrence.ReminderId);
        if (plan is null || !plan.Enabled || plan.PlanVersion != occurrence.PlanVersion)
        {
            return ManualRetryOutcome.PlanChanged;
        }

        Occurrences[index] = occurrence with
        {
            Status = ReminderOccurrenceStatus.Pending,
            ScheduledAtUtc = now,
            DeliveryAttempts = 0,
            RecoveryAttempts = Math.Max(occurrence.RecoveryAttempts, 1),
            ManualAttempts = occurrence.ManualAttempts + 1,
            DeliveryOwner = null,
            DeliveryLeaseUntilUtc = null,
            Action = "manual-retry",
            ActionAtUtc = now,
            UpdatedAtUtc = now,
        };
        return ManualRetryOutcome.Reset;
    }

    public bool BeginScheduleRevocation(Guid reminderId, string reason, DateTimeOffset now)
    {
        var index = Plans.FindIndex(plan => plan.Id == reminderId);
        if (index < 0 || string.IsNullOrEmpty(Plans[index].ScheduledTag))
        {
            return false;
        }

        var plan = Plans[index];
        Plans[index] = plan with { ScheduleState = "revoking", ScheduleUpdatedAtUtc = now };
        EnqueueCancellation(reminderId, plan.PlanVersion, plan.ScheduledTag!, reason, now);
        return true;
    }

    public bool CompleteScheduleRevocation(Guid reminderId, string notificationTag, DateTimeOffset now)
    {
        var index = Plans.FindIndex(plan => plan.Id == reminderId
            && plan.ScheduledTag == notificationTag
            && plan.ScheduleState is "revoking" or "uncertain");
        if (index < 0)
        {
            return false;
        }

        Plans[index] = Plans[index] with
        {
            DeliveryOwner = "app",
            ScheduledTag = null,
            ScheduledAtUtc = null,
            ScheduleState = null,
            SchedulePlanVersion = null,
            ScheduleError = null,
            ScheduleUpdatedAtUtc = now,
        };
        return true;
    }

    public bool MarkScheduleUncertain(Guid reminderId, string notificationTag, string error, DateTimeOffset now)
    {
        var index = Plans.FindIndex(plan => plan.Id == reminderId
            && plan.ScheduledTag == notificationTag
            && plan.ScheduleState == "revoking");
        if (index < 0)
        {
            return false;
        }

        Plans[index] = Plans[index] with
        {
            ScheduleState = "uncertain",
            ScheduleError = error,
            ScheduleUpdatedAtUtc = now,
        };
        return true;
    }

    public bool MarkScheduledDeliveryUnknown(Guid reminderId, int planVersion, Guid occurrenceId, DateTimeOffset now)
    {
        var planIndex = Plans.FindIndex(plan => plan.Id == reminderId
            && plan.Enabled
            && plan.PlanVersion == planVersion
            && plan.ScheduleState is "scheduled" or "registering");
        var occurrenceIndex = Occurrences.FindIndex(o => o.Id == occurrenceId
            && o.ReminderId == reminderId
            && o.PlanVersion == planVersion
            && o.Status == ReminderOccurrenceStatus.Pending);
        if (planIndex < 0 || occurrenceIndex < 0)
        {
            return false;
        }

        Plans[planIndex] = Plans[planIndex] with
        {
            ScheduleState = "failed",
            DeliveryOwner = "app",
            ScheduleError = "scheduled-tag-missing",
            ScheduleUpdatedAtUtc = now,
        };
        Occurrences[occurrenceIndex] = Occurrences[occurrenceIndex] with
        {
            Status = ReminderOccurrenceStatus.Uncertain,
            DeliveryAttempts = Math.Max(Occurrences[occurrenceIndex].DeliveryAttempts, 1),
            LastError = "scheduled-tag-missing",
            DeliveryOwner = null,
            DeliveryLeaseUntilUtc = null,
            UpdatedAtUtc = now,
        };
        return true;
    }

    public bool SnoozeOccurrence(Guid occurrenceId, DateTimeOffset newReminderAtUtc, string optionId, DateTimeOffset now)
    {
        var index = Occurrences.FindIndex(o => o.Id == occurrenceId
            && o.Status is ReminderOccurrenceStatus.Pending
                or ReminderOccurrenceStatus.Requested
                or ReminderOccurrenceStatus.Uncertain);
        if (index < 0)
        {
            return false;
        }

        var occurrence = Occurrences[index];
        var planIndex = Plans.FindIndex(p => p.Id == occurrence.ReminderId);
        if (planIndex < 0 || !Plans[planIndex].Enabled || Plans[planIndex].PlanVersion != occurrence.PlanVersion)
        {
            return false;
        }

        Occurrences[index] = occurrence with
        {
            Status = ReminderOccurrenceStatus.Snoozed,
            Action = $"snooze:{optionId}",
            ActionAtUtc = now,
            DeliveryOwner = null,
            DeliveryLeaseUntilUtc = null,
            UpdatedAtUtc = now,
        };

        Plans[planIndex] = Plans[planIndex] with
        {
            ReminderAtUtc = newReminderAtUtc,
            PlanVersion = Plans[planIndex].PlanVersion + 1,
            UpdatedAtUtc = now,
        };
        return true;
    }

    private bool UpdateDeliveryResult(
        Guid occurrenceId,
        string owner,
        ReminderOccurrenceStatus status,
        string? error,
        DateTimeOffset now)
    {
        var index = Occurrences.FindIndex(o => o.Id == occurrenceId
            && o.DeliveryOwner == owner
            && o.Status is ReminderOccurrenceStatus.Pending or ReminderOccurrenceStatus.Uncertain);
        if (index < 0)
        {
            return false;
        }

        Occurrences[index] = Occurrences[index] with
        {
            Status = status,
            LastError = error,
            DeliveryOwner = null,
            DeliveryLeaseUntilUtc = null,
            UpdatedAtUtc = now,
        };
        return true;
    }
}

internal static class TestData
{
    public static readonly DateTimeOffset CapturedAt = new(2026, 10, 21, 6, 0, 0, TimeSpan.Zero);

    public static Capture Capture(string text = "刚处理完登录故障，明天下午三点提醒我复核，记住客户要求保留原始日志。") =>
        new(
            Guid.NewGuid(),
            text,
            CapturedAt,
            "Asia/Shanghai",
            CaptureParseStatus.Pending,
            null,
            CapturedAt);

    public static ParseDraft Draft(Guid captureId, params DraftCandidate[] candidates) => new(
        Guid.NewGuid(),
        captureId,
        1,
        DraftStatus.AwaitingConfirmation,
        candidates,
        null,
        "deepseek-flash",
        "p1",
        "2026-10-21.1",
        1,
        null,
        CapturedAt,
        CapturedAt);
}

internal sealed class InMemoryDraftRepository : IParseDraftRepository
{
    public List<ParseDraft> Drafts { get; } = new();

    public void Add(ParseDraft draft) => Drafts.Add(draft);

    public ParseDraft? GetById(Guid id) => Drafts.FirstOrDefault(draft => draft.Id == id);

    public ParseDraft? GetLatestForCapture(Guid captureId) =>
        Drafts.Where(draft => draft.CaptureId == captureId).OrderByDescending(draft => draft.Version).FirstOrDefault();

    public IReadOnlyList<ParseDraft> GetByStatus(DraftStatus status) =>
        Drafts.Where(draft => draft.Status == status).ToArray();

    public int CountByStatus(DraftStatus status) => Drafts.Count(draft => draft.Status == status);

    public bool SaveEditState(Guid draftId, int expectedEditVersion, string editStateJson, DateTimeOffset updatedAt)
    {
        var index = Drafts.FindIndex(draft => draft.Id == draftId
            && draft.Status == DraftStatus.AwaitingConfirmation
            && draft.EditVersion == expectedEditVersion);
        if (index < 0)
        {
            return false;
        }

        Drafts[index] = Drafts[index] with
        {
            EditStateJson = editStateJson,
            EditVersion = Drafts[index].EditVersion + 1,
            UpdatedAtUtc = updatedAt,
        };
        return true;
    }

    public void SaveParsingResult(
        Guid draftId,
        DraftStatus status,
        IReadOnlyList<DraftCandidate> candidates,
        string? rawAiJson,
        string? model,
        string? promptVersion,
        string? schemaVersion,
        int attemptCount,
        string? error,
        DateTimeOffset updatedAt)
    {
        Replace(draftId, draft => draft with
        {
            Status = status,
            Candidates = candidates,
            RawAiJson = rawAiJson,
            Model = model,
            PromptVersion = promptVersion,
            SchemaVersion = schemaVersion,
            AttemptCount = attemptCount,
            LastError = error,
            UpdatedAtUtc = updatedAt,
        });
    }

    public void UpdateStatus(Guid draftId, DraftStatus status, string? error, DateTimeOffset updatedAt)
    {
        Replace(draftId, draft => draft with { Status = status, LastError = error, UpdatedAtUtc = updatedAt });
    }

    private void Replace(Guid draftId, Func<ParseDraft, ParseDraft> update)
    {
        var index = Drafts.FindIndex(draft => draft.Id == draftId);
        if (index >= 0)
        {
            Drafts[index] = update(Drafts[index]);
        }
    }
}

internal sealed class ScriptedAiParser : IAiParser
{
    private readonly Queue<Func<AiParseResponse>> _responses = new();

    public int CallCount { get; private set; }

    public void EnqueueSuccess(params DraftCandidate[] candidates) =>
        _responses.Enqueue(() => new AiParseResponse("{}", "deepseek-flash", candidates, Array.Empty<string>()));

    public void EnqueueFailure(AiParseException exception) =>
        _responses.Enqueue(() => throw exception);

    public Task<AiParseResponse> ParseAsync(AiParseRequest request, CancellationToken cancellationToken)
    {
        CallCount++;
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("没有预置的解析响应。");
        }

        return Task.FromResult(_responses.Dequeue()());
    }
}
