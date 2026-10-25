using MemoDesk.Core.Domain;
using MemoDesk.Core.Time;

namespace MemoDesk.Core.Drafting;

/// <summary>
/// 候选编辑 → 正式候选的转换（核心规则，便于跨平台单测）。
/// 未修改的字段原样复用；工作时间按最终输入判定，明确填写的结束日期优先。
/// </summary>
public static class DraftCandidateEditor
{
    public static DraftCandidate Apply(
        DraftCandidate original,
        DraftCandidateEditState edit,
        TimeZoneInfo timeZone,
        DateOnly captureDate)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(timeZone);

        var candidate = new DraftCandidate
        {
            SourceId = original.SourceId,
            Type = edit.Type,
            Content = edit.Content.Trim(),
            DueAtUtc = original.DueAtUtc,
            Priority = edit.Priority,
            Tags = ParseTags(edit.TagsText),
            Issues = ParseLines(edit.IssuesText),
        };

        switch (edit.Type)
        {
            case ItemType.Todo:
            case ItemType.Memo:
                BuildTodoOrMemo(candidate, original, edit, timeZone, captureDate);
                break;
            case ItemType.WorkLog:
                BuildWorkLog(candidate, original, edit, captureDate);
                break;
            default:
                candidate.ValidationErrors.Add("未知事项类型。");
                break;
        }

        return candidate;
    }

    private static void BuildTodoOrMemo(
        DraftCandidate candidate,
        DraftCandidate original,
        DraftCandidateEditState edit,
        TimeZoneInfo timeZone,
        DateOnly captureDate)
    {
        candidate.BusinessDate = edit.Date
            ?? (edit.Type == ItemType.Memo ? original.BusinessDate ?? captureDate : null);

        candidate.RemindAtUtc = null;
        if (edit.RemindDate is not { } remindDate)
        {
            return;
        }

        var time = new TimeOnly(9, 0);
        if (!string.IsNullOrWhiteSpace(edit.RemindTime))
        {
            if (!TimeOnly.TryParse(edit.RemindTime, out time))
            {
                candidate.ValidationErrors.Add("提醒时间格式无效（应为 HH:mm），请修改后再确认。");
                return;
            }
        }

        candidate.RemindAtUtc = BusinessTime.Combine(remindDate, time, timeZone);
    }

    private static void BuildWorkLog(
        DraftCandidate candidate,
        DraftCandidate original,
        DraftCandidateEditState edit,
        DateOnly captureDate)
    {
        var result = WorkTimeRules.Build(
            original.WorkTime,
            edit.Date,
            edit.WorkStart,
            edit.WorkEnd,
            edit.WorkEndDate,
            captureDate);

        candidate.WorkTime = result.WorkTime;
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            candidate.ValidationErrors.Add(result.Error);
        }
    }

    private static List<string> ParseTags(string? tagsText) => (tagsText ?? string.Empty)
        .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(10)
        .ToList();

    private static List<string> ParseLines(string? text) => (text ?? string.Empty)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Take(5)
        .ToList();
}
