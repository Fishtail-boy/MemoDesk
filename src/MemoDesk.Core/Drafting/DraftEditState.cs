using MemoDesk.Core.Domain;

namespace MemoDesk.Core.Drafting;

/// <summary>
/// 草稿编辑状态：保存用户原始输入（包括非法时间文本）与脏字段标记，
/// 校验错误不落库，每次由输入重新计算。
/// </summary>
public sealed class DraftEditState
{
    public List<DraftCandidateEditState> Candidates { get; set; } = new();
}

public sealed class DraftCandidateEditState
{
    public Guid SourceId { get; set; }

    public ItemType Type { get; set; }

    public string Content { get; set; } = string.Empty;

    public DateOnly? Date { get; set; }

    public DateOnly? RemindDate { get; set; }

    public string RemindTime { get; set; } = string.Empty;

    public string WorkStart { get; set; } = string.Empty;

    public string WorkEnd { get; set; } = string.Empty;

    public DateOnly? WorkEndDate { get; set; }

    public string TagsText { get; set; } = string.Empty;

    public ItemPriority? Priority { get; set; }

    public string IssuesText { get; set; } = string.Empty;

    /// <summary>用户实际修改过的字段名（界面层维护），用于审计与恢复。</summary>
    public List<string> DirtyFields { get; set; } = new();
}
