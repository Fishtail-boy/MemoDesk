using MemoDesk.Core.Items;
using MemoDesk.Core.Domain;

namespace MemoDesk.Core.Items;

/// <summary>事项状态操作（设计 2.4）：完成的联动事务在存储层保证。</summary>
public sealed class ItemService
{
    private readonly IItemStore _store;
    private readonly TimeProvider _clock;

    public ItemService(IItemStore store, TimeProvider clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Item? Get(Guid id) => _store.GetById(id);

    /// <summary>任一事项发生变化后触发，供界面刷新多个窗口。</summary>
    public event Action? ItemChanged;

    public void Update(Item item)
    {
        _store.Update(item);
        NotifyChanged();
    }

    /// <summary>内容、工作时间和提醒的原子保存；成功后只发一次变更事件。</summary>
    public MemoEditSaveResult UpdateWithReminder(Item item, ReminderEditIntent reminder)
    {
        var result = _store.UpdateWithReminder(item, reminder, _clock.GetUtcNow());
        if (result.IsSuccess)
        {
            NotifyChanged();
        }

        return result;
    }

    public bool Complete(Guid itemId, Guid? reminderOccurrenceId = null)
    {
        var changed = _store.TrySetStatus(itemId, ItemStatus.Completed, _clock.GetUtcNow(), reminderOccurrenceId);
        if (changed)
        {
            NotifyChanged();
        }

        return changed;
    }

    public bool Reopen(Guid itemId)
    {
        var changed = _store.TrySetStatus(itemId, ItemStatus.Open, _clock.GetUtcNow());
        if (changed)
        {
            NotifyChanged();
        }

        return changed;
    }

    public bool Archive(Guid itemId)
    {
        var changed = _store.TrySetStatus(itemId, ItemStatus.Archived, _clock.GetUtcNow());
        if (changed)
        {
            NotifyChanged();
        }

        return changed;
    }

    public bool Delete(Guid itemId)
    {
        var changed = _store.TrySetStatus(itemId, ItemStatus.Deleted, _clock.GetUtcNow());
        if (changed)
        {
            NotifyChanged();
        }

        return changed;
    }

    /// <summary>由确认入库等非 ItemService 路径调用，通知界面刷新。</summary>
    public void NotifyChanged() => ItemChanged?.Invoke();
}
