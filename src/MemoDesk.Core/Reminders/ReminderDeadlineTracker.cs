namespace MemoDesk.Core.Reminders;

/// <summary>短相对提醒在运行中使用单调时钟；UTC 目标仍用于持久化及重启恢复。</summary>
public sealed class ReminderDeadlineTracker
{
    private readonly Dictionary<(Guid Id, int Version), (long Timestamp, TimeSpan Remaining)> _deadlines = new();
    public TimeSpan Remaining(ReminderPlan plan, TimeProvider clock)
    {
        var wallRemaining = plan.ReminderAtUtc - clock.GetUtcNow();
        if (plan.Source != "LocalExplicit" || plan.RelativeSeconds is not (> 0 and <= 300)) return wallRemaining;
        var key = (plan.Id, plan.PlanVersion);
        if (!_deadlines.TryGetValue(key, out var deadline))
        {
            deadline = (clock.GetTimestamp(), wallRemaining);
            _deadlines[key] = deadline;
        }
        return deadline.Remaining - clock.GetElapsedTime(deadline.Timestamp);
    }
    public void Retain(IReadOnlyList<ReminderPlan> plans)
    {
        var active = plans.Select(p => (p.Id, p.PlanVersion)).ToHashSet();
        foreach (var key in _deadlines.Keys.Where(k => !active.Contains(k)).ToArray()) _deadlines.Remove(key);
    }
}
