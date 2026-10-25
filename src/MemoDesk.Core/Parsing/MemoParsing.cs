using MemoDesk.Core.Domain;

namespace MemoDesk.Core.Parsing;

/// <summary>后台整理后的单条备忘记事；类型不再由 AI 决定，统一按备忘处理。</summary>
public sealed class MemoParseCandidate
{
    /// <summary>稳定候选 ID；同一原文的同一操作版本重试时保持一致。</summary>
    public Guid SourceId { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>该候选对应的原文片段，必须能在原文中原样定位。</summary>
    public string? SourceText { get; set; }

    /// <summary>原文片段中的提醒时间表达，必须能由 SourceText 支持。</summary>
    public string? RemindText { get; set; }

    public MemoReminderIntent ReminderIntent { get; set; }

    public MemoReminderPrecision ReminderPrecision { get; set; }

    /// <summary>原文在录入时表达的未来提醒；为空表示不提醒。</summary>
    public DateTimeOffset? RemindAtUtc { get; set; }

    /// <summary>本地规则判定为即时提醒时标记；应用时必须重新校验，不能作为绕过证据的凭证。</summary>
    public bool RemindNow { get; set; }

    /// <summary>原文明确描述已发生工作的发生时间；不是提醒时间。</summary>
    public WorkTimeInfo? WorkTime { get; set; }

    public ItemPriority? Priority { get; set; }

    public List<string> Tags { get; set; } = new();

    public List<string> Issues { get; set; } = new();

    /// <summary>本地校验错误；只影响对应字段，不应阻止原文备忘保存。</summary>
    public List<string> ValidationErrors { get; set; } = new();
}

public enum MemoReminderIntent
{
    None = 0,
    ExplicitReminder = 1,
    ImplicitFutureEvent = 2,
    Immediate = 3,
    Forbidden = 4,
}

public enum MemoReminderPrecision
{
    None = 0,
    DateOnly = 1,
    Period = 2,
    Exact = 3,
    Relative = 4,
}

public enum MemoParseStatus
{
    Parsing = 0,
    AwaitingApply = 1,
    Applied = 2,
    Failed = 3,
    Superseded = 4,
    Skipped = 5,
}

public sealed record MemoParseResponse(
    string RawJson,
    string Model,
    IReadOnlyList<MemoParseCandidate> Candidates,
    IReadOnlyList<string> Notes);

public interface IMemoAiParser
{
    Task<MemoParseResponse> ParseMemoAsync(AiParseRequest request, CancellationToken cancellationToken);
}

/// <summary>后台整理结果。先保存草稿结果，再由独立事务应用；崩溃后可先应用已有结果。</summary>
public sealed record MemoParseRecord(
    Guid Id,
    Guid CaptureId,
    int OperationVersion,
    int Version,
    MemoParseStatus Status,
    IReadOnlyList<MemoParseCandidate> Candidates,
    string? RawAiJson,
    string? Model,
    string? PromptVersion,
    string? SchemaVersion,
    int AttemptCount,
    string? LastError,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? AppliedAtUtc = null,
    Guid? AppliedMemoId = null);
