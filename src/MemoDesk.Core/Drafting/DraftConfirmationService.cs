using MemoDesk.Core.Domain;
using MemoDesk.Core.Drafting;
using MemoDesk.Core.Items;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Time;
using MemoDesk.Core.Timeline;

namespace MemoDesk.Core.Drafting;

public enum DraftConfirmOutcome
{
    Confirmed = 0,
    AlreadyConfirmed = 1,
    VersionConflict = 2,
    DraftNotFound = 3,
    Invalid = 4,
}

public sealed record DraftConfirmResult(DraftConfirmOutcome Outcome, IReadOnlyList<string> Errors)
{
    public bool IsSuccess => Outcome == DraftConfirmOutcome.Confirmed;
}

/// <summary>
/// 把待确认草稿转成正式事项（设计 2.2 / 3.1 / 7.3）。
/// 一次确认原子保存全部候选项；提醒时间过期时要求用户明确处理，不静默改写。
/// </summary>
public sealed class DraftConfirmationService
{
    private readonly IItemStore _store;
    private readonly TimeProvider _clock;

    public DraftConfirmationService(IItemStore store, TimeProvider clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public DraftConfirmResult Confirm(
        ParseDraft draft,
        Core.Capturing.Capture capture,
        IReadOnlyList<DraftCandidate> candidates,
        int? expectedEditVersion = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(capture);

        if (expectedEditVersion is { } editVersion && editVersion != draft.EditVersion)
        {
            return new(DraftConfirmOutcome.VersionConflict, new[] { "草稿已被其他编辑更新，请刷新后重试。" });
        }

        if (draft.Status == DraftStatus.Confirmed)
        {
            return new(DraftConfirmOutcome.AlreadyConfirmed, Array.Empty<string>());
        }

        if (draft.Status != DraftStatus.AwaitingConfirmation)
        {
            return new(DraftConfirmOutcome.Invalid, new[] { "草稿当前状态不可确认。" });
        }

        if (candidates.Count == 0)
        {
            return new(DraftConfirmOutcome.Invalid, new[] { "至少保留一个事项后再确认。" });
        }

        var validationErrors = candidates
            .SelectMany(candidate => candidate.ValidationErrors)
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (validationErrors.Length > 0)
        {
            return new(DraftConfirmOutcome.Invalid, validationErrors);
        }

        var now = _clock.GetUtcNow();

        var errors = new List<string>();
        var items = new List<Item>();
        var reminders = new List<ReminderPlan>();
        var events = new List<TimelineEvent>();

        foreach (var candidate in candidates)
        {
            var content = candidate.Content.Trim();
            if (content.Length == 0)
            {
                errors.Add("存在内容为空的事项，请补充内容或移除。");
                continue;
            }

            if (candidate.Type is ItemType.Todo or ItemType.Memo
                && candidate.RemindAtUtc is { } remindAt
                && remindAt <= now)
            {
                errors.Add($"“{content}”的提醒时间已过，请修改提醒时间或取消提醒。");
                continue;
            }

            if (candidate.Type == ItemType.WorkLog
                && candidate.WorkTime is { Precision: WorkTimePrecision.Range, Start: { } start, End: { } end } workTime)
            {
                var endDate = workTime.EndDate ?? workTime.Date;
                if (endDate.ToDateTime(end) <= workTime.Date.ToDateTime(start))
                {
                    errors.Add($"“{content}”的结束时间不能早于或等于开始时间。");
                    continue;
                }
            }

            items.Add(BuildItem(draft, candidate, content, now));
            AddReminderIfNeeded(candidate, items[^1].Id, now, reminders);
        }

        if (errors.Count > 0)
        {
            return new(DraftConfirmOutcome.Invalid, errors);
        }

        var expectedEdit = expectedEditVersion ?? draft.EditVersion;
        var outcome = _store.ConfirmDraft(
            draft.Id,
            draft.Version,
            items,
            reminders,
            events,
            now,
            expectedEdit);
        return new(Map(outcome), Array.Empty<string>());
    }

    private static DraftConfirmOutcome Map(DraftConfirmationOutcome outcome) => outcome switch
    {
        DraftConfirmationOutcome.Confirmed => DraftConfirmOutcome.Confirmed,
        DraftConfirmationOutcome.AlreadyConfirmed => DraftConfirmOutcome.AlreadyConfirmed,
        DraftConfirmationOutcome.VersionConflict => DraftConfirmOutcome.VersionConflict,
        DraftConfirmationOutcome.InvalidState => DraftConfirmOutcome.Invalid,
        _ => DraftConfirmOutcome.DraftNotFound,
    };

    private static Item BuildItem(ParseDraft draft, DraftCandidate candidate, string content, DateTimeOffset now)
    {
        var tags = NormalizeTags(candidate.Tags);
        var common = new Item
        {
            Id = Guid.NewGuid(),
            CaptureId = draft.CaptureId,
            CandidateSourceId = candidate.SourceId,
            Type = candidate.Type,
            Content = content,
            Tags = tags,
            Priority = candidate.Priority,
            Status = ItemStatus.Open,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        return candidate.Type switch
        {
            ItemType.Todo => common with
            {
                ItemDate = candidate.BusinessDate,
                DueAtUtc = candidate.DueAtUtc,
            },
            ItemType.WorkLog => common with
            {
                WorkTime = candidate.WorkTime,
            },
            ItemType.Memo => common with
            {
                MemoDate = candidate.BusinessDate,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(candidate), candidate.Type, "未知事项类型。"),
        };
    }

    private static void AddReminderIfNeeded(
        DraftCandidate candidate,
        Guid itemId,
        DateTimeOffset now,
        List<ReminderPlan> reminders)
    {
        if (candidate.Type is not (ItemType.Todo or ItemType.Memo) || candidate.RemindAtUtc is not { } remindAt)
        {
            return;
        }

        reminders.Add(new ReminderPlan(
            Id: Guid.NewGuid(),
            ItemId: itemId,
            ReminderAtUtc: remindAt,
            Enabled: true,
            PlanVersion: 1,
            CreatedAtUtc: now,
            UpdatedAtUtc: now));
    }

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags)
    {
        if (tags is null)
        {
            return Array.Empty<string>();
        }

        return tags
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Select(tag => tag.Length > 30 ? tag[..30] : tag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
    }
}
