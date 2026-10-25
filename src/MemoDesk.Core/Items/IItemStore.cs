using MemoDesk.Core.Domain;
using MemoDesk.Core.Drafting;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Timeline;

namespace MemoDesk.Core.Items;

public enum DraftConfirmationOutcome
{
    Confirmed = 0,
    AlreadyConfirmed = 1,
    VersionConflict = 2,
    DraftNotFound = 3,
    InvalidState = 4,
}

/// <summary>
/// 事项的持久化入口。确认草稿与状态变化必须在一个事务里完成（设计 9）。
/// </summary>
public interface IItemStore
{
    Item? GetById(Guid id);

    /// <summary>更新可编辑字段（内容、标签、优先级、日期与工作发生时间）。版本不匹配时抛出冲突。</summary>
    void Update(Item item);

    /// <summary>
    /// 原子保存人工编辑：内容/标签/日期/工作时间和提醒变更在同一事务内提交。
    /// Unchanged 不触碰提醒；Set/Clear 校验打开窗口时的计划 ID/版本。
    /// </summary>
    MemoEditSaveResult UpdateWithReminder(Item item, ReminderEditIntent reminder, DateTimeOffset now);

    /// <summary>
    /// 变更状态并产生联动：完成时写时间线事件并停用提醒；归档/删除停用提醒。
    /// 若提供提醒实例 ID，同时把该实例标记为已完成（通知按钮路径）。
    /// </summary>
    bool TrySetStatus(Guid itemId, ItemStatus status, DateTimeOffset now, Guid? reminderOccurrenceId = null);

    /// <summary>
    /// 原子确认草稿：校验版本与状态后，写入事项、提醒计划、时间线事件，
    /// 并把草稿置为已确认。任何一步失败整体回滚。
    /// </summary>
    DraftConfirmationOutcome ConfirmDraft(
        Guid draftId,
        int expectedDraftVersion,
        IReadOnlyList<Item> items,
        IReadOnlyList<ReminderPlan> reminders,
        IReadOnlyList<TimelineEvent> events,
        DateTimeOffset now,
        int expectedEditVersion = 0);
}
