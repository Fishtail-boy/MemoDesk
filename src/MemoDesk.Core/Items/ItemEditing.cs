using MemoDesk.Core.Domain;

namespace MemoDesk.Core.Items;

public enum ReminderEditKind
{
    Unchanged = 0,
    Set = 1,
    Clear = 2,
}

/// <summary>
/// 一次编辑对提醒的明确意图。ExpectedReminderId/PlanVersion 来自打开编辑窗口时的活动计划；
/// 仅改内容时 Kind=Unchanged，不允许隐式触碰提醒表。
/// </summary>
public sealed record ReminderEditIntent(
    ReminderEditKind Kind,
    DateTimeOffset? TargetAtUtc,
    Guid? ExpectedReminderId,
    int? ExpectedPlanVersion,
    bool HadActivePlanAtOpen);

public enum MemoEditOutcome
{
    Success = 0,
    VersionConflict = 1,
    ReminderVersionConflict = 2,
    ReminderInvalid = 3,
    NotFound = 4,
    Failure = 5,
}

public sealed record MemoEditSaveResult(MemoEditOutcome Outcome, string? Error)
{
    public bool IsSuccess => Outcome == MemoEditOutcome.Success;

    public static MemoEditSaveResult Success() => new(MemoEditOutcome.Success, null);
}

public sealed class ItemVersionConflictException : Exception
{
    public ItemVersionConflictException(Guid itemId)
        : base("备忘已被其他窗口或 AI 修改，当前内容没有覆盖最新版本。")
    {
        ItemId = itemId;
    }

    public Guid ItemId { get; }
}
