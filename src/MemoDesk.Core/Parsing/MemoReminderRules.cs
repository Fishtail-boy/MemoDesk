using System.Globalization;
using System.Text.RegularExpressions;
using MemoDesk.Core.Time;

namespace MemoDesk.Core.Parsing;

public sealed record MemoReminderDecision(
    bool Allow,
    MemoReminderIntent Intent,
    MemoReminderPrecision Precision,
    DateTimeOffset? TargetAtUtc,
    string? Reason)
{
    public bool Immediate => Intent == MemoReminderIntent.Immediate;
}

/// <summary>
/// 逐候选的提醒本地防线：候选必须有可定位的原文片段和时间表达，
/// 明确时间必须能按捕获时区计算出目标，并与模型目标一致。
/// </summary>
public static class MemoReminderRules
{
    private static readonly Regex ChineseNumberPattern = new("[零〇一二两三四五六七八九十]+", RegexOptions.Compiled);
    private static readonly Regex NegationPattern = new(
        @"不提醒|不用提醒|无需提醒|不要提醒|别提醒|不必提醒|不用叫我|别叫我",
        RegexOptions.Compiled);
    private static readonly Regex PrefixNegationPattern = new(
        @"(?:不要|不用|无需|不必|别)(?:再)?(?:提醒|叫我)$",
        RegexOptions.Compiled);
    private static readonly Regex GlobalNegationPattern = new(
        @"(都|全部|全都|一律).{0,4}(不|别|不用|无需).{0,2}(提醒|叫我)",
        RegexOptions.Compiled);
    private static readonly Regex ImmediatePattern = new(
        @"(现在|马上|立刻|立即|立马).{0,4}(提醒|叫我)",
        RegexOptions.Compiled);
    private static readonly Regex FullDatePattern = new(
        @"(?<y>\d{4})[-/](?<mo>\d{1,2})[-/](?<d>\d{1,2})",
        RegexOptions.Compiled);
    private static readonly Regex MonthDayPattern = new(
        @"(?<mo>\d{1,2})月(?<d>\d{1,2})[日号]?",
        RegexOptions.Compiled);
    private static readonly Regex TimePattern = new(
        @"(?<h>\d{1,2})(?:[:：](?<mi>\d{2})|(?:点|时)(?:(?<half>半)|(?<quarter>一刻钟|一刻|1刻钟|1刻)|(?<m2>\d{1,2})分?)?)",
        RegexOptions.Compiled);
    private static readonly Regex WeekPattern = new(
        @"(?<prefix>下|本|这)?(?:周|星期)(?<day>[一二三四五六日天1-7])",
        RegexOptions.Compiled);
    private static readonly Regex WeekTokenPattern = new(
        @"(?<prefix>下|本|这)?(?<week>周|星期)(?<day>[一二三四五六日天])",
        RegexOptions.Compiled);
    private static readonly Regex PeriodPattern = new(
        @"凌晨|早上|早晨|上午|中午|下午|傍晚|晚上",
        RegexOptions.Compiled);
    private static readonly Regex PastWorkPattern = new(
        @"昨天|前天|刚才|刚刚|已(经)?(完成|处理|联系|提交|做)|处理完",
        RegexOptions.Compiled);
    private static readonly Regex FutureMarkerPattern = new(
        @"明天|后天|下周|下次|以后|稍后|待会|将来|之后",
        RegexOptions.Compiled);

    public static MemoReminderDecision Evaluate(
        string rawText,
        MemoParseCandidate candidate,
        DateTimeOffset capturedAt,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return EvaluateAll(rawText, new[] { candidate }, capturedAt, timeZone)[0];
    }

    public static IReadOnlyList<MemoReminderDecision> EvaluateAll(
        string rawText,
        IReadOnlyList<MemoParseCandidate> candidates,
        DateTimeOffset capturedAt,
        TimeZoneInfo timeZone)
    {
        var spans = candidates
            .Select(candidate => FindSourceSpan(rawText, candidate.SourceText))
            .ToArray();
        if (HasAmbiguousSources(spans))
        {
            return candidates
                .Select(_ => Deny(MemoReminderIntent.None, "多个候选的原文片段重叠，无法确认提醒归属。"))
                .ToArray();
        }

        var decisions = new List<MemoReminderDecision>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var scope = BuildScope(rawText, spans, index);
            decisions.Add(EvaluateCore(rawText, candidates[index], scope, capturedAt, timeZone));
        }

        return decisions;
    }

    public static bool HasAmbiguousSources(string rawText, IReadOnlyList<MemoParseCandidate> candidates) =>
        HasAmbiguousSources(candidates.Select(candidate => FindSourceSpan(rawText, candidate.SourceText)).ToArray());

    private static (int Start, int End) FindSourceSpan(string rawText, string? sourceText)
    {
        var source = sourceText?.Trim() ?? string.Empty;
        if (source.Length == 0)
        {
            return (-1, -1);
        }

        var start = rawText.IndexOf(source, StringComparison.Ordinal);
        return start < 0 ? (-1, -1) : (start, start + source.Length);
    }

    private static bool HasAmbiguousSources(IReadOnlyList<(int Start, int End)> spans)
    {
        for (var i = 0; i < spans.Count; i++)
        {
            if (spans[i].Start < 0)
            {
                continue;
            }

            for (var j = i + 1; j < spans.Count; j++)
            {
                if (spans[j].Start < 0)
                {
                    continue;
                }

                if (spans[i].Start < spans[j].End && spans[j].Start < spans[i].End)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string BuildScope(
        string rawText,
        IReadOnlyList<(int Start, int End)> spans,
        int index)
    {
        var (start, end) = spans[index];
        if (start < 0 || end < 0)
        {
            return string.Empty;
        }

        var sentenceStart = 0;
        for (var cursor = start - 1; cursor >= 0; cursor--)
        {
            if (rawText[cursor] is '。' or '！' or '？' or '\n' or '；' or ';')
            {
                sentenceStart = cursor + 1;
                break;
            }
        }

        // 候选之前的否定只有在它是本句第一个有来源片段的事项时才归属于它；
        // “A，不提醒，B”中的“不提醒”属于 A，不应被 B 继承。
        var hasEarlierCandidate = spans.Any(span => span.Start >= 0
            && span.Start < start
            && span.Start >= sentenceStart);
        var scopeStart = hasEarlierCandidate ? start : sentenceStart;

        // 但“不要提醒 B”是 B 的前置否定：即使 A 在前，也要从 A 的尾部
        // 切走并纳入 B 的范围。
        var prefixNegationStart = FindPrefixNegationStart(rawText, start);
        if (hasEarlierCandidate && prefixNegationStart >= sentenceStart)
        {
            scopeStart = Math.Min(scopeStart, prefixNegationStart);
        }

        var scopeEnd = rawText.Length;
        for (var cursor = end; cursor < rawText.Length; cursor++)
        {
            if (rawText[cursor] is '。' or '！' or '？' or '\n' or '；' or ';')
            {
                scopeEnd = cursor;
                break;
            }
        }

        for (var other = 0; other < spans.Count; other++)
        {
            if (other == index || spans[other].Start < 0)
            {
                continue;
            }

            var otherStart = spans[other].Start;
            if (otherStart > start && otherStart < scopeEnd)
            {
                var otherPrefixStart = FindPrefixNegationStart(rawText, otherStart);
                scopeEnd = Math.Min(
                    scopeEnd,
                    otherPrefixStart >= start ? otherPrefixStart : otherStart);
            }
        }

        return rawText[scopeStart..Math.Max(scopeStart, scopeEnd)];
    }

    private static int FindPrefixNegationStart(string rawText, int beforeIndex)
    {
        var cursor = beforeIndex - 1;
        while (cursor >= 0 && char.IsWhiteSpace(rawText[cursor]))
        {
            cursor--;
        }

        if (cursor < 0)
        {
            return -1;
        }

        var end = cursor + 1;
        var start = Math.Max(0, end - 24);
        var segment = rawText[start..end];
        var match = PrefixNegationPattern.Match(segment);
        return match.Success ? start + match.Index : -1;
    }

    private static MemoReminderDecision EvaluateCore(
        string rawText,
        MemoParseCandidate candidate,
        string scopeText,
        DateTimeOffset capturedAt,
        TimeZoneInfo timeZone)
    {
        if (string.IsNullOrWhiteSpace(candidate.SourceText)
            || string.IsNullOrWhiteSpace(candidate.RemindText))
        {
            return Deny(MemoReminderIntent.None, "缺少可核对的原文时间片段。");
        }

        var raw = NormalizeEvidence(rawText);
        var source = NormalizeEvidence(candidate.SourceText);
        var remind = NormalizeEvidence(candidate.RemindText);
        var scope = NormalizeEvidence(scopeText);
        if (source.Length == 0 || !raw.Contains(source, StringComparison.Ordinal))
        {
            return Deny(MemoReminderIntent.None, "候选的原文片段不能在原文中定位。");
        }

        if (remind.Length == 0 || !source.Contains(remind, StringComparison.Ordinal))
        {
            return Deny(MemoReminderIntent.None, "候选的时间表达不属于该原文片段。");
        }

        if (GlobalNegationPattern.IsMatch(raw) || NegationPattern.IsMatch(scope))
        {
            return Deny(MemoReminderIntent.Forbidden, "原文明确表示不提醒。");
        }

        if ((candidate.WorkTime is { } workTime
                && workTime.Date < BusinessTime.DateOf(capturedAt, timeZone)
                || PastWorkPattern.IsMatch(source))
            && !FutureMarkerPattern.IsMatch(source))
        {
            return Deny(MemoReminderIntent.Forbidden, "原文描述的是已发生工作，不能作为未来提醒。");
        }

        if (ImmediatePattern.IsMatch(scope))
        {
            if (candidate.RemindAtUtc is { } modelTarget
                && Math.Abs((modelTarget - capturedAt).TotalMinutes) > 1)
            {
                return Deny(MemoReminderIntent.Immediate, "模型目标不是捕获时刻，拒绝即时提醒。");
            }

            return new MemoReminderDecision(
                true,
                MemoReminderIntent.Immediate,
                MemoReminderPrecision.Exact,
                capturedAt,
                null);
        }

        if (!TryComputeTarget(candidate.SourceText ?? string.Empty, capturedAt, timeZone, out var expected, out var precision, out var timeReason))
        {
            return Deny(MemoReminderIntent.None, timeReason);
        }

        if (expected <= capturedAt)
        {
            return Deny(MemoReminderIntent.ExplicitReminder, "时间表达在录入前已经过去。");
        }

        if (candidate.RemindAtUtc is not { } target)
        {
            return Deny(MemoReminderIntent.ExplicitReminder, "模型没有给出可核对的目标时间。");
        }

        if (Math.Abs((target - expected).TotalSeconds) > (precision == MemoReminderPrecision.Relative ? 1 : 60))
        {
            return Deny(MemoReminderIntent.ExplicitReminder, "模型目标与原文时间表达不一致。");
        }

        var intent = NegationPattern.IsMatch(scope)
            ? MemoReminderIntent.Forbidden
            : scope.Contains("提醒", StringComparison.Ordinal) || scope.Contains("叫我", StringComparison.Ordinal)
                ? MemoReminderIntent.ExplicitReminder
                : MemoReminderIntent.ImplicitFutureEvent;

        return intent == MemoReminderIntent.Forbidden
            ? Deny(MemoReminderIntent.Forbidden, "原文明确表示不提醒。")
            : new MemoReminderDecision(true, intent, precision, expected, null);
    }

    public static string NormalizeEvidence(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : Regex.Replace(text, @"\s+", string.Empty, RegexOptions.None, TimeSpan.FromMilliseconds(100));

    public static bool HasAmbiguousSources(IReadOnlyList<MemoParseCandidate> candidates)
    {
        var sources = candidates
            .Select(candidate => NormalizeEvidence(candidate.SourceText))
            .Where(source => source.Length > 0)
            .ToArray();
        for (var i = 0; i < sources.Length; i++)
        {
            for (var j = i + 1; j < sources.Length; j++)
            {
                if (sources[i] == sources[j]
                    || sources[i].Contains(sources[j], StringComparison.Ordinal)
                    || sources[j].Contains(sources[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static MemoReminderDecision Deny(MemoReminderIntent intent, string reason) =>
        new(false, intent, MemoReminderPrecision.None, null, reason);

    private static bool TryComputeTarget(
        string source,
        DateTimeOffset capturedAt,
        TimeZoneInfo timeZone,
        out DateTimeOffset expected,
        out MemoReminderPrecision precision,
        out string reason)
    {
        expected = default;
        precision = MemoReminderPrecision.None;
        reason = "原文没有可计算的明确时间。";

        var normalized = NormalizeChineseNumbers(source);
        if (RelativeReminderTime.TryFind(source, out var duration, out _))
        {
            try
            {
                expected = capturedAt + duration;
            }
            catch (ArgumentOutOfRangeException)
            {
                reason = "相对时间超出支持范围。";
                return false;
            }
            precision = MemoReminderPrecision.Relative;
            reason = string.Empty;
            return true;
        }

        var time = TimePattern.Match(normalized);
        if (!time.Success)
        {
            // 只有日期、模糊时段或仅有“提醒”词，不能猜测钟点。
            reason = "原文没有明确钟点或可计算的相对时长。";
            return false;
        }

        var trailing = normalized[(time.Index + time.Length)..].TrimStart();
        if (trailing.StartsWith("刻", StringComparison.Ordinal))
        {
            reason = "刻钟表达无法完整识别，不能截取局部钟点猜目标。";
            return false;
        }

        if (!int.TryParse(time.Groups["h"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour)
            || hour is < 0 or > 23)
        {
            reason = "原文钟点无法识别。";
            return false;
        }

        var minute = 0;
        if (time.Groups["mi"].Success)
        {
            minute = int.Parse(time.Groups["mi"].Value, CultureInfo.InvariantCulture);
        }
        else if (time.Groups["half"].Success)
        {
            minute = 30;
        }
        else if (time.Groups["quarter"].Success)
        {
            minute = 15;
        }
        else if (time.Groups["m2"].Success)
        {
            minute = int.Parse(time.Groups["m2"].Value, CultureInfo.InvariantCulture);
        }

        if (minute is < 0 or > 59)
        {
            reason = "原文分钟无法识别。";
            return false;
        }

        var period = PeriodPattern.Match(normalized[..Math.Min(time.Index, normalized.Length)]);
        if (period.Success)
        {
            hour = period.Value switch
            {
                "凌晨" => hour == 12 ? 0 : hour,
                "早上" or "早晨" or "上午" => hour == 12 ? 0 : hour,
                "中午" => hour is >= 1 and < 11 ? 12 + hour : hour,
                "下午" or "傍晚" or "晚上" when hour is >= 1 and < 12 => hour + 12,
                _ => hour,
            };
        }
        else if (hour is >= 1 and <= 7)
        {
            // 没有时段词时不能猜上午/下午；“明天七点”保持 07:00，但这是明确钟点。
        }

        var date = BusinessTime.DateOf(capturedAt, timeZone);
        var fullDate = FullDatePattern.Match(normalized);
        var monthDay = MonthDayPattern.Match(normalized);
        var week = WeekPattern.Match(normalized);
        if (week.Success)
        {
            var dayNumber = WeekdayNumber(week.Groups["day"].Value);
            if (dayNumber == 0)
            {
                reason = "星期表达无法识别。";
                return false;
            }

            var captureDate = date;
            var offsetToMonday = ((int)captureDate.DayOfWeek + 6) % 7;
            var thisMonday = captureDate.AddDays(-offsetToMonday);
            var prefix = week.Groups["prefix"].Value;
            var baseMonday = thisMonday;
            if (prefix == "下")
            {
                var daysToNextMonday = ((int)DayOfWeek.Monday - (int)captureDate.DayOfWeek + 7) % 7;
                baseMonday = captureDate.AddDays(daysToNextMonday == 0 ? 7 : daysToNextMonday);
            }

            date = baseMonday.AddDays(dayNumber - 1);
        }
        else if (fullDate.Success)
        {
            date = new DateOnly(
                int.Parse(fullDate.Groups["y"].Value, CultureInfo.InvariantCulture),
                int.Parse(fullDate.Groups["mo"].Value, CultureInfo.InvariantCulture),
                int.Parse(fullDate.Groups["d"].Value, CultureInfo.InvariantCulture));
        }
        else if (monthDay.Success)
        {
            date = new DateOnly(
                date.Year,
                int.Parse(monthDay.Groups["mo"].Value, CultureInfo.InvariantCulture),
                int.Parse(monthDay.Groups["d"].Value, CultureInfo.InvariantCulture));
        }
        else if (normalized.Contains("下周", StringComparison.Ordinal)
            || normalized.Contains("下星期", StringComparison.Ordinal))
        {
            reason = "星期表达不完整，无法计算日期。";
            return false;
        }
        else if (normalized.Contains("后天", StringComparison.Ordinal))
        {
            date = date.AddDays(2);
        }
        else if (normalized.Contains("明天", StringComparison.Ordinal))
        {
            date = date.AddDays(1);
        }

        var local = new DateTime(date.Year, date.Month, date.Day, hour, minute, 0, DateTimeKind.Unspecified);
        expected = BusinessTime.ToUtc(local, timeZone);
        precision = MemoReminderPrecision.Exact;
        reason = string.Empty;
        return true;
    }

    private static int WeekdayNumber(string value) => value switch
    {
        "一" or "1" => 1,
        "二" or "2" => 2,
        "三" or "3" => 3,
        "四" or "4" => 4,
        "五" or "5" => 5,
        "六" or "6" => 6,
        "日" or "天" or "7" => 7,
        _ => 0,
    };

    public static string NormalizeChineseNumbers(string text)
    {
        // 保留“周/星期 + 日”的边界，避免“下周一十五点”被合并成“下周15点”。
        var separated = WeekTokenPattern.Replace(
            text,
            match => $"{match.Groups["prefix"].Value}{match.Groups["week"].Value}{match.Groups["day"].Value} ");
        return ChineseNumberPattern.Replace(separated, match => ChineseToArabic(match.Value).ToString(CultureInfo.InvariantCulture));
    }

    private static int ChineseToArabic(string text)
    {
        var digits = new Dictionary<char, int>
        {
            ['零'] = 0, ['〇'] = 0, ['一'] = 1, ['二'] = 2, ['两'] = 2,
            ['三'] = 3, ['四'] = 4, ['五'] = 5, ['六'] = 6, ['七'] = 7,
            ['八'] = 8, ['九'] = 9,
        };

        if (text == "十")
        {
            return 10;
        }

        var ten = text.IndexOf('十');
        if (ten >= 0)
        {
            var tens = ten == 0 ? 1 : digits[text[0]];
            var ones = ten == text.Length - 1 ? 0 : digits[text[^1]];
            return tens * 10 + ones;
        }

        return digits.TryGetValue(text[0], out var value) ? value : 0;
    }
}
