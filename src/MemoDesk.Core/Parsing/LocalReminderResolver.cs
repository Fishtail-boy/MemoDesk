using System.Text.RegularExpressions;
using MemoDesk.Core.Time;

namespace MemoDesk.Core.Parsing;

public sealed record LocalReminderResolution(string Content, DateTimeOffset TargetAtUtc,
    string SourceText, long RelativeSeconds);

/// <summary>只接受单一、明确的相对提醒命令；多事项和歧义继续交给后台整理。</summary>
public static class LocalReminderResolver
{
    private static readonly Regex Command = new(
        @"^(?:请)?(?<time>.+?(?:之后|以后|后))\s*(?:请)?提醒(?:我)?(?:一下)?\s*(?<content>.+)$",
        RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(250));
    private static readonly Regex Ambiguous = new(@"[，,。；;\r\n]|不(?:要|用|必)?(?:再)?提醒|无需提醒|别(?:再)?提醒|叫我",
        RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(250));

    public static LocalReminderResolution? Resolve(string raw, DateTimeOffset capturedAt, TimeZoneInfo zone)
    {
        // Local recognition is optional: it must never prevent the original memo being saved.
        try { return ResolveCore(raw, capturedAt, zone); }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static LocalReminderResolution? ResolveCore(string raw, DateTimeOffset capturedAt, TimeZoneInfo zone)
    {
        var text = raw.Trim();
        if (text.Length > 2000 || Ambiguous.IsMatch(text)) return null;
        var match = Command.Match(text);
        if (!match.Success || match.Groups["content"].Value.Contains("提醒", StringComparison.Ordinal)) return null;
        var timeText = match.Groups["time"].Value.Trim();
        if (!RelativeReminderTime.TryFind(timeText, out var duration, out var expression)
            || MemoReminderRules.NormalizeChineseNumbers(timeText) != expression) return null;
        DateTimeOffset target;
        try { target = capturedAt + duration; }
        catch (ArgumentOutOfRangeException) { return null; }
        var candidate = new MemoParseCandidate { Content = match.Groups["content"].Value.Trim(),
            SourceText = text, RemindText = timeText, RemindAtUtc = target };
        var decision = MemoReminderRules.Evaluate(text, candidate, capturedAt, zone);
        return decision.Allow ? new(candidate.Content, target, timeText, (long)duration.TotalSeconds) : null;
    }
}
