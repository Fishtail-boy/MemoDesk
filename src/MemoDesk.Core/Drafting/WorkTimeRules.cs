using MemoDesk.Core.Domain;

namespace MemoDesk.Core.Drafting;

public sealed record WorkTimeBuildResult(WorkTimeInfo WorkTime, string? Error);

/// <summary>
/// 工作时间构建规则（草稿编辑与正式编辑共用）：
/// 未修改字段原样复用；日期单独变化时平移；明确填写的结束日期优先；
/// 结束不能早于/等于开始；自动推断跨午夜与明确结束日期共用最终校验。
/// </summary>
public static class WorkTimeRules
{
    public static WorkTimeBuildResult Build(
        WorkTimeInfo? original,
        DateOnly? date,
        string? startText,
        string? endText,
        DateOnly? explicitEndDate,
        DateOnly fallbackDate)
    {
        var startInput = startText?.Trim() ?? string.Empty;
        var endInput = endText?.Trim() ?? string.Empty;
        var originalStart = original?.Start?.ToString("HH:mm") ?? string.Empty;
        var originalEnd = original?.End?.ToString("HH:mm") ?? string.Empty;

        var timesUnchanged = string.Equals(startInput, originalStart, StringComparison.Ordinal)
            && string.Equals(endInput, originalEnd, StringComparison.Ordinal)
            && explicitEndDate == original?.EndDate;
        var dateChanged = original is not null
            && date is { } newDate
            && newDate != original.Date;

        if (original is not null && timesUnchanged && !dateChanged)
        {
            return new WorkTimeBuildResult(original, null);
        }

        if (original is not null && timesUnchanged && dateChanged)
        {
            var delta = date!.Value.DayNumber - original.Date.DayNumber;
            return new WorkTimeBuildResult(
                original with
                {
                    Date = date.Value,
                    EndDate = original.EndDate?.AddDays(delta),
                },
                null);
        }

        var workDate = date ?? original?.Date ?? fallbackDate;
        TimeOnly? start = null;
        TimeOnly? end = null;
        string? error = null;

        if (startInput.Length > 0)
        {
            if (TimeOnly.TryParse(startInput, out var parsedStart))
            {
                start = parsedStart;
            }
            else
            {
                error = "开始时间格式无效（应为 HH:mm）。";
            }
        }

        if (endInput.Length > 0)
        {
            if (TimeOnly.TryParse(endInput, out var parsedEnd))
            {
                end = parsedEnd;
            }
            else
            {
                error ??= "结束时间格式无效（应为 HH:mm）。";
            }
        }

        DateOnly? endDate = explicitEndDate;
        WorkTimePrecision precision;
        if (start is not null && end is not null)
        {
            precision = WorkTimePrecision.Range;
            if (endDate is null && end < start)
            {
                endDate = workDate.AddDays(1);
            }

            var endAt = (endDate ?? workDate).ToDateTime(end.Value);
            var startAt = workDate.ToDateTime(start.Value);
            if (endAt <= startAt)
            {
                error ??= "结束时间不能早于或等于开始时间。";
            }
        }
        else if (start is not null)
        {
            precision = WorkTimePrecision.Exact;
        }
        else if (end is not null || !string.IsNullOrWhiteSpace(original?.OriginalText))
        {
            precision = WorkTimePrecision.Period;
        }
        else
        {
            precision = WorkTimePrecision.Date;
        }

        return new WorkTimeBuildResult(
            new WorkTimeInfo(
                workDate,
                start,
                end,
                original?.OriginalText,
                precision,
                endDate),
            error);
    }
}
