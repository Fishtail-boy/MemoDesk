using System.Text.Json;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Time;

namespace MemoDesk.Core.Parsing;

/// <summary>B 版 JSON 映射：所有结果都是备忘候选，只保留原文能支持的时间字段。</summary>
public static class MemoResponseMapper
{
    public static MemoParseResponse Map(string rawContent, string model, AiParseRequest request)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            throw new AiParseException(AiFailureKind.InvalidResponse, "AI 返回了空内容。");
        }

        var json = StripCodeFence(rawContent);
        EnvelopeDto? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EnvelopeDto>(json, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            throw new AiParseException(AiFailureKind.InvalidResponse, "AI 返回的 JSON 无法解析。", ex);
        }

        if (envelope?.Items is null || envelope.Items.Count == 0)
        {
            throw new AiParseException(AiFailureKind.InvalidResponse, "AI 未返回任何备忘内容。");
        }

        if (envelope.Items.Count > 10)
        {
            throw new AiParseException(AiFailureKind.InvalidResponse, "AI 返回的候选过多（>10），已拒绝。");
        }

        var timeZone = BusinessTime.Resolve(request.TimeZoneId);
        var captureDate = BusinessTime.DateOf(request.CapturedAtUtc, timeZone);
        var candidates = new List<MemoParseCandidate>();

        foreach (var item in envelope.Items)
        {
            var candidate = MapItem(item, timeZone, captureDate, request);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        if (candidates.Count == 0)
        {
            throw new AiParseException(AiFailureKind.InvalidResponse, "AI 返回的内容均未通过校验。");
        }

        MarkAmbiguousSources(candidates);

        var decisions = MemoReminderRules.EvaluateAll(
            request.RawText,
            candidates,
            request.CapturedAtUtc,
            timeZone);
        for (var index = 0; index < candidates.Count; index++)
        {
            ApplyDecision(candidates[index], decisions[index]);
        }

        var notes = (envelope.Notes ?? new List<string>())
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .Select(note => note.Trim())
            .Take(10)
            .ToArray();

        return new MemoParseResponse(rawContent, model, candidates, notes);
    }

    private static void MarkAmbiguousSources(IReadOnlyList<MemoParseCandidate> candidates)
    {
        if (candidates.Count < 2)
        {
            return;
        }

        var normalized = candidates
            .Select(candidate => MemoReminderRules.NormalizeEvidence(candidate.SourceText))
            .ToArray();

        for (var i = 0; i < candidates.Count; i++)
        {
            if (normalized[i].Length == 0)
            {
                continue;
            }

            for (var j = i + 1; j < candidates.Count; j++)
            {
                if (normalized[j].Length == 0)
                {
                    continue;
                }

                if (normalized[i] == normalized[j]
                    || normalized[i].Contains(normalized[j], StringComparison.Ordinal)
                    || normalized[j].Contains(normalized[i], StringComparison.Ordinal))
                {
                    ClearReminder(candidates[i], "多个候选的原文片段重叠，无法确认提醒归属。");
                    ClearReminder(candidates[j], "多个候选的原文片段重叠，无法确认提醒归属。");
                }
            }
        }
    }

    private static void ClearReminder(MemoParseCandidate candidate, string reason)
    {
        candidate.RemindAtUtc = null;
        candidate.RemindNow = false;
        candidate.ReminderPrecision = MemoReminderPrecision.None;
        candidate.Issues.Add(reason);
    }

    private static void ApplyDecision(MemoParseCandidate candidate, MemoReminderDecision decision)
    {
        candidate.ReminderIntent = decision.Intent;
        candidate.ReminderPrecision = decision.Precision;
        candidate.RemindNow = decision.Allow && decision.Intent == MemoReminderIntent.Immediate;
        if (decision.Allow)
        {
            candidate.RemindAtUtc = decision.TargetAtUtc;
            return;
        }

        candidate.RemindAtUtc = null;
        if (!string.IsNullOrWhiteSpace(decision.Reason)
            && !candidate.Issues.Contains(decision.Reason, StringComparer.Ordinal))
        {
            candidate.Issues.Add(decision.Reason!);
        }
    }

    private static MemoParseCandidate? MapItem(
        ItemDto item,
        TimeZoneInfo timeZone,
        DateOnly captureDate,
        AiParseRequest request)
    {
        var content = item.Content?.Trim();
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        var candidate = new MemoParseCandidate
        {
            Content = content,
            SourceText = item.SourceText?.Trim(),
            RemindText = item.RemindText?.Trim(),
            Priority = ParsePriority(item.Priority),
            Tags = NormalizeTags(item.Tags),
        };

        candidate.Issues.AddRange((item.Issues ?? new List<string>())
            .Where(issue => !string.IsNullOrWhiteSpace(issue))
            .Select(issue => issue.Trim())
            .Take(5));

        candidate.RemindAtUtc = ParseInstant(item.RemindAt, timeZone);
        candidate.WorkTime = ParseWorkTime(item.WorkTime, timeZone, captureDate);


        candidate.WorkTime = ParseWorkTime(item.WorkTime, timeZone, captureDate);
        return candidate;
    }

    private static WorkTimeInfo? ParseWorkTime(WorkTimeDto? dto, TimeZoneInfo timeZone, DateOnly captureDate)
    {
        if (dto is null)
        {
            return null;
        }

        var date = ParseDate(dto.Date) ?? captureDate;
        var start = ParseTime(dto.Start);
        var end = ParseTime(dto.End);
        var endDate = ParseDate(dto.EndDate);
        var text = string.IsNullOrWhiteSpace(dto.Text) ? null : dto.Text.Trim();

        var precision = start is not null && end is not null
            ? WorkTimePrecision.Range
            : start is not null
                ? WorkTimePrecision.Exact
                : text is not null
                    ? WorkTimePrecision.Period
                    : WorkTimePrecision.Date;

        if (end is not null && start is null)
        {
            precision = WorkTimePrecision.Period;
        }

        if (endDate is null && start is not null && end is not null && end < start)
        {
            endDate = date.AddDays(1);
        }

        return new WorkTimeInfo(date, start, end, text, precision, endDate);
    }

    private static ItemPriority? ParsePriority(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" => ItemPriority.Low,
        "normal" or "medium" => ItemPriority.Normal,
        "high" => ItemPriority.High,
        _ => null,
    };

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParse(value.Trim(), out var date) ? date : null;
    }

    private static TimeOnly? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (TimeOnly.TryParse(value.Trim(), out var time))
        {
            return time;
        }

        return DateTime.TryParse(value.Trim(), out var dateTime) ? TimeOnly.FromDateTime(dateTime) : null;
    }

    private static DateTimeOffset? ParseInstant(string? value, TimeZoneInfo timeZone)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.EndsWith('Z') || text.Contains('+') || text.LastIndexOf('-') > 9)
        {
            if (DateTimeOffset.TryParse(text, out var withOffset))
            {
                return withOffset.ToUniversalTime();
            }
        }

        if (DateTime.TryParse(text, out var local))
        {
            return BusinessTime.ToUtc(local, timeZone);
        }

        return null;
    }

    private static List<string> NormalizeTags(List<string>? tags) => tags is null
        ? new List<string>()
        : tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length <= 30)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

    private static string StripCodeFence(string content)
    {
        var text = content.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
        {
            return text;
        }

        var body = text[(firstNewline + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        if (closing >= 0)
        {
            body = body[..closing];
        }

        return body.Trim();
    }

    private sealed class EnvelopeDto
    {
        public List<ItemDto>? Items { get; set; }

        public List<string>? Notes { get; set; }
    }

    private sealed class ItemDto
    {
        public string? Content { get; set; }

        public string? SourceText { get; set; }

        public string? RemindText { get; set; }

        public string? RemindAt { get; set; }

        public WorkTimeDto? WorkTime { get; set; }

        public string? Priority { get; set; }

        public List<string>? Tags { get; set; }

        public List<string>? Issues { get; set; }
    }

    private sealed class WorkTimeDto
    {
        public string? Date { get; set; }

        public string? Start { get; set; }

        public string? End { get; set; }

        public string? EndDate { get; set; }

        public string? Text { get; set; }

        public string? Precision { get; set; }
    }
}
