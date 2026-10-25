using MemoDesk.Core.Time;

namespace MemoDesk.Core.Reminders;

public sealed record SnoozeResult(bool Success, DateTimeOffset? NewReminderAtUtc, string? Error);

/// <summary>本轮应投递的实例；IsRecovery 表示来自崩溃窗口或不确定结果。</summary>
public sealed record ReminderCandidate(ReminderOccurrence Occurrence, bool IsRecovery, bool ElapsedDeadline = false);

/// <summary>
/// 提醒调度（设计 5）：数据库计划为准；投递采用原子领取 + 租约，
/// 已交接不再自动补发；恢复候选由展示层按投递证据决定，未知交接只核对历史。
/// </summary>
public sealed class ReminderScheduler
{
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(60);

    private readonly IReminderRepository _repository;

    public ReminderScheduler(IReminderRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// 收集本轮候选：到期未投递的 Pending、租约过期的崩溃窗口、可恢复一次的 Uncertain，
    /// 以及新增的到期计划实例。不会修改状态；领取由 <see cref="TryClaim"/> 原子完成。
    /// </summary>
    public IReadOnlyList<ReminderCandidate> CollectCandidates(
        DateTimeOffset nowUtc,
        IReadOnlyList<SnoozeOption> currentOptions,
        IReadOnlyList<ReminderPlan>? monotonicDue = null,
        bool requireElapsedForShortRelative = false)
    {
        ArgumentNullException.ThrowIfNull(currentOptions);

        var candidates = new Dictionary<Guid, ReminderCandidate>();

        foreach (var occurrence in _repository.GetPendingDue(nowUtc))
        {
            if (requireElapsedForShortRelative && occurrence.ManualAttempts == 0
                && !occurrence.NotificationTag.StartsWith("manual-", StringComparison.Ordinal)
                && _repository.GetReminder(occurrence.ReminderId) is { } pendingPlan
                && IsShortRelative(pendingPlan) && !HasElapsed(pendingPlan, monotonicDue)) continue;
            candidates[occurrence.Id] = new ReminderCandidate(occurrence, IsRecovery: false);
        }

        foreach (var occurrence in _repository.GetRecoverable(nowUtc))
        {
            candidates[occurrence.Id] = new ReminderCandidate(occurrence, IsRecovery: true);
        }

        var duePlans = _repository.GetActiveDue(nowUtc).Concat(monotonicDue ?? Array.Empty<ReminderPlan>())
            .DistinctBy(p => (p.Id, p.PlanVersion));
        foreach (var snapshot in duePlans)
        {
            var plan = _repository.GetReminder(snapshot.Id);
            if (plan is null || !plan.Enabled || plan.PlanVersion != snapshot.PlanVersion
                || plan.DeliveryOwner is "windows" or "windows-pending") continue;
            // 正式展示层的短时相对提醒只接受精细计时器的到期信号，防止 UTC 前拨使周期扫描提前投递。
            if (requireElapsedForShortRelative && IsShortRelative(plan) && !HasElapsed(plan, monotonicDue)) continue;
            if (_repository.GetBlockingOccurrenceForPlan(plan.Id, plan.PlanVersion) is { } existing)
            {
                if (existing.Status == ReminderOccurrenceStatus.Pending && existing.DeliveryAttempts == 0
                    && IsShortRelative(plan) && HasElapsed(plan, monotonicDue))
                    candidates[existing.Id] = new ReminderCandidate(existing, false, existing.ScheduledAtUtc > nowUtc);
                continue;
            }

            var occurrence = new ReminderOccurrence(
                Id: Guid.NewGuid(),
                ReminderId: plan.Id,
                ItemId: plan.ItemId,
                ScheduledAtUtc: plan.ReminderAtUtc,
                Status: ReminderOccurrenceStatus.Pending,
                NotificationTag: $"memo-{plan.ItemId:N}-{plan.PlanVersion}",
                OptionsSnapshot: currentOptions.ToArray(),
                RequestedAtUtc: null,
                Action: null,
                ActionAtUtc: null,
                AttemptCount: 0,
                LastError: null,
                CreatedAtUtc: nowUtc,
                UpdatedAtUtc: nowUtc,
                PlanVersion: plan.PlanVersion);

            if (!_repository.TryAddOccurrence(occurrence))
            {
                // 另一个调度器已经为同一计划版本建档；不要把它当成候选。
                continue;
            }

            var elapsedDeadline = plan.ReminderAtUtc > nowUtc && plan.Source == "LocalExplicit"
                && plan.RelativeSeconds is > 0 and <= 300
                && monotonicDue?.Any(p => p.Id == plan.Id && p.PlanVersion == plan.PlanVersion) == true;
            candidates[occurrence.Id] = new ReminderCandidate(occurrence, IsRecovery: false, elapsedDeadline);
        }

        return candidates.Values
            .OrderBy(candidate => candidate.Occurrence.ScheduledAtUtc)
            .ToArray();
    }

    /// <summary>兼容旧调用：返回本轮候选实例（不区分恢复）。</summary>
    public IReadOnlyList<ReminderOccurrence> CollectDue(
        DateTimeOffset nowUtc,
        IReadOnlyList<SnoozeOption> currentOptions) =>
        CollectCandidates(nowUtc, currentOptions)
            .Select(candidate => candidate.Occurrence)
            .ToArray();

    /// <summary>原子领取投递权；多扫描并发时只有一个成功。</summary>
    public bool TryClaim(ReminderCandidate candidate, string owner, DateTimeOffset nowUtc) =>
        candidate.IsRecovery
            ? _repository.TryClaimRecovery(candidate.Occurrence.Id, owner, nowUtc, DefaultLeaseDuration)
            : candidate.ElapsedDeadline
                ? _repository.TryClaimElapsedDelivery(candidate.Occurrence.Id, owner, nowUtc, DefaultLeaseDuration)
                : _repository.TryClaimDelivery(candidate.Occurrence.Id, owner, nowUtc, DefaultLeaseDuration);

    private static bool IsShortRelative(ReminderPlan plan) => plan.Source == "LocalExplicit" && plan.RelativeSeconds is > 0 and <= 300;
    private static bool HasElapsed(ReminderPlan plan, IReadOnlyList<ReminderPlan>? elapsed) =>
        elapsed?.Any(p => p.Id == plan.Id && p.PlanVersion == plan.PlanVersion) == true;

    public bool MarkHandedOff(Guid occurrenceId, string owner, string channel, DateTimeOffset nowUtc) =>
        _repository.MarkHandedOff(occurrenceId, owner, channel, nowUtc);

    public bool MarkFailed(Guid occurrenceId, string owner, string? error, DateTimeOffset nowUtc) =>
        _repository.MarkDeliveryFailed(occurrenceId, owner, error, nowUtc);

    public bool MarkUncertain(Guid occurrenceId, string owner, string? error, DateTimeOffset nowUtc) =>
        _repository.MarkDeliveryUncertain(occurrenceId, owner, error, nowUtc);

    /// <summary>用户显式请求重发；不复用自动恢复预算。</summary>
    public ManualRetryOutcome ResetForManualDelivery(Guid occurrenceId, DateTimeOffset nowUtc) =>
        _repository.TryResetOccurrenceForManualDelivery(occurrenceId, nowUtc);

    /// <summary>
    /// 兼容旧调用：直接把实例标为已请求（无领取者/版本校验）。仅用于迁移/测试，
    /// 正式投递必须走 TryClaim + MarkHandedOff。
    /// </summary>
    public void MarkRequested(Guid occurrenceId, DateTimeOffset nowUtc) =>
        _repository.MarkOccurrenceStatus(
            occurrenceId, ReminderOccurrenceStatus.Requested, null, nowUtc);

    public SnoozeResult Snooze(
        Guid occurrenceId,
        string optionId,
        TimeZoneInfo timeZone,
        DateTimeOffset nowUtc)
    {
        var occurrence = _repository.GetOccurrence(occurrenceId);
        if (occurrence is null)
        {
            return new(false, null, "提醒实例不存在。");
        }

        if (occurrence.Status is not (ReminderOccurrenceStatus.Pending
            or ReminderOccurrenceStatus.Requested
            or ReminderOccurrenceStatus.Uncertain))
        {
            return new(false, null, "该提醒已经处理或已失效。");
        }

        var option = occurrence.OptionsSnapshot.FirstOrDefault(item => item.Id == optionId);
        if (option is null)
        {
            return new(false, null, "稍后提醒选项已失效。");
        }

        var next = SnoozeCalculator.Compute(option, nowUtc, timeZone);
        var ok = _repository.SnoozeOccurrence(occurrenceId, next, option.Id, nowUtc);
        return ok
            ? new(true, next, null)
            : new(false, null, "稍后提醒失败：提醒已变更或事项已完成。");
    }

    /// <summary>补提醒判定：实例到点后超过 2 分钟才展示，标记为补提醒。</summary>
    public static bool IsCatchUp(ReminderOccurrence occurrence, DateTimeOffset nowUtc) =>
        nowUtc - occurrence.ScheduledAtUtc > TimeSpan.FromMinutes(2);
}
