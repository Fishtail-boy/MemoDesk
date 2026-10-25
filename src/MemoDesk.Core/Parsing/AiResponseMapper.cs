using System.Text.Json;
using MemoDesk.Core.Drafting;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Time;

namespace MemoDesk.Core.Parsing;

/// <summary>
/// 把 DeepSeek 返回的 JSON 映射为候选事项并做本地业务校验（设计 4）。
/// JSON 格式正确不代表字段可靠：类型、时间合法性、无时间不设提醒等规则在这里兜底。
/// </summary>
public static class AiResponseMapper
{
    public static AiParseResponse Map(string rawContent, string model, AiParseRequest request)
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
            throw new AiParseException(AiFailureKind.InvalidResponse, "AI 未返回任何事项候选。");
        }

        if (envelope.Items.Count > 10)
        {
            throw new AiParseException(AiFailureKind.InvalidResponse, "AI 返回的候选事项过多（>10），已拒绝。");
        }

        var timeZone = BusinessTime.Resolve(request.TimeZoneId);
        var captureDate = BusinessTime.DateOf(request.CapturedAtUtc, timeZone);
        var candidates = new List<DraftCandidate>();

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
            throw new AiParseException(AiFailureKind.InvalidResponse, "AI 返回的候选项均未通过校验。");
        }

        var notes = (envelope.Notes ?? new List<string>())
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .Select(note => note.Trim())
            .Take(10)
            .ToArray();

        return new AiParseResponse(rawContent, model, candidates, notes);
    }

    private static DraftCandidate? MapItem(ItemDto item, TimeZoneInfo timeZone, DateOnly captureDate, AiParseRequest request)
    {
        var content = item.Content?.Trim();
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        var type = ParseType(item.Type);
        if (type is null)
        {
            return null;
        }

        var candidate = new DraftCandidate
        {
            Type = type.Value,
            Content = content,
            Priority = ParsePriority(item.Priority),
            Tags = NormalizeTags(item.Tags),
        };

        candidate.Issues.AddRange((item.Issues ?? new List<string>())
            .Where(issue => !string.IsNullOrWhiteSpace(issue))
            .Select(issue => issue.Trim())
            .Take(5));

        switch (type.Value)
        {
            case ItemType.Todo:
                candidate.BusinessDate = ParseDate(item.BusinessDate);
                candidate.DueAtUtc = ParseInstant(item.DueAt, timeZone);
                candidate.RemindAtUtc = ParseInstant(item.RemindAt, timeZone);
                break;

            case ItemType.Memo:
                candidate.BusinessDate = ParseDate(item.BusinessDate) ?? captureDate;
                candidate.RemindAtUtc = ParseInstant(item.RemindAt, timeZone);
                break;

            case ItemType.WorkLog:
                candidate.WorkTime = ParseWorkTime(item.WorkTime, timeZone, captureDate);
                candidate.RemindAtUtc = null; // 工作记录永不设提醒
                break;
        }

        if (candidate.RemindAtUtc is { } remind && remind <= request.CapturedAtUtc)
        {
            candidate.Issues.Add("提醒时间早于捕获时间，请确认或调整。");
        }

        return candidate;
    }

    private static WorkTimeInfo ParseWorkTime(WorkTimeDto? dto, TimeZoneInfo timeZone, DateOnly captureDate)
    {
        if (dto is null)
        {
            return new WorkTimeInfo(captureDate, null, null, null, WorkTimePrecision.Date);
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
            endDate = date.AddDays(1); // 跨日区间
        }

        return new WorkTimeInfo(date, start, end, text, precision, endDate);
    }

    private static ItemType? ParseType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "todo" => ItemType.Todo,
        "work_log" or "worklog" or "work" => ItemType.WorkLog,
        "memo" => ItemType.Memo,
        _ => null,
    };

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

        var text = value.Trim();
        if (TimeOnly.TryParse(text, out var time))
        {
            return time;
        }

        return DateTime.TryParse(text, out var dateTime) ? TimeOnly.FromDateTime(dateTime) : null;
    }

    private static DateTimeOffset? ParseInstant(string? value, TimeZoneInfo timeZone)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        // 带偏移量（...+08:00 / ...Z）：按其自身偏移解析，再统一为 UTC。
        if (text.EndsWith('Z') || text.Contains('+') || text.LastIndexOf('-') > 9)
        {
            if (DateTimeOffset.TryParse(text, out var withOffset))
            {
                return withOffset.ToUniversalTime();
            }
        }

        // 无偏移量：视为捕获时区的本地时刻。
        if (DateTime.TryParse(text, out var local))
        {
            return BusinessTime.ToUtc(local, timeZone);
        }

        return null;
    }

    private static List<string> NormalizeTags(List<string>? tags)
    {
        if (tags is null)
        {
            return new List<string>();
        }

        return tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length <= 30)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

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
        public string? Type { get; set; }

        public string? Content { get; set; }

        public string? BusinessDate { get; set; }

        public string? DueAt { get; set; }

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
