using MemoDesk.Core.Domain;

namespace MemoDesk.Core.Drafting;

/// <summary>草稿状态（设计 2.2）：待解析→解析中→待确认→已确认；失败可重试或手动整理。</summary>
public enum DraftStatus
{
    Parsing = 0,
    AwaitingConfirmation = 1,
    Failed = 2,
    Confirmed = 3,
    Discarded = 4,

    /// <summary>重新解析后旧版本被新版本取代。</summary>
    Superseded = 5,

    /// <summary>解析结果已经应用到正式备忘。</summary>
    Applied = 6,
}

/// <summary>
/// 候选事项。解析器生成，用户在待确认界面编辑；SourceId 稳定，用于确认入库的幂等标识。
/// </summary>
public sealed class DraftCandidate
{
    public Guid SourceId { get; set; } = Guid.NewGuid();

    public ItemType Type { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>待办的事项日期 / 备忘的备忘日期（业务日期）。工作记录日期在 WorkTime 中。</summary>
    public DateOnly? BusinessDate { get; set; }

    /// <summary>待办的截止时间（UTC）。</summary>
    public DateTimeOffset? DueAtUtc { get; set; }

    /// <summary>提醒时间（UTC）；仅待办与备忘可设置。</summary>
    public DateTimeOffset? RemindAtUtc { get; set; }

    public WorkTimeInfo? WorkTime { get; set; }

    public ItemPriority? Priority { get; set; }

    public List<string> Tags { get; set; } = new();

    /// <summary>需要用户确认的提示（过期时间、歧义等），不阻塞保存但界面上突出显示。</summary>
    public List<string> Issues { get; set; } = new();

    /// <summary>本地校验错误（非法时间等）。存在时确认必须被拒绝，不能静默兜底。</summary>
    public List<string> ValidationErrors { get; set; } = new();
}

/// <summary>一条解析草稿（设计 7.2）。人工编辑结果与 AI 原始结果分开保存。</summary>
public sealed record ParseDraft(
    Guid Id,
    Guid CaptureId,
    int Version,
    DraftStatus Status,
    IReadOnlyList<DraftCandidate> Candidates,
    string? RawAiJson,
    string? Model,
    string? PromptVersion,
    string? SchemaVersion,
    int AttemptCount,
    string? LastError,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? EditStateJson = null,
    int EditVersion = 0,
    int OperationVersion = 1,
    DateTimeOffset? AppliedAtUtc = null,
    Guid? AppliedMemoId = null);
