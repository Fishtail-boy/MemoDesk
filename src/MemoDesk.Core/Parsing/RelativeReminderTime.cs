using System.Globalization;
using System.Text.RegularExpressions;

namespace MemoDesk.Core.Parsing;

/// <summary>相对时长的共同解释器；完整消费组合单位，不把 1分30秒 截成局部。</summary>
public static class RelativeReminderTime
{
    private const string Unit = @"秒钟|seconds?|secs?|秒|s|分钟|minutes?|mins?|分|m|小时|钟头|hours?|hrs?|h|天|days?|d";
    private static readonly Regex Expression = new(
        @"(?<![\dA-Za-z.+-])(?<duration>(?:\d+\s*(?:" + Unit + @")\s*)+)(?:之后|以后|后)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex Component = new(@"(?<n>\d+)\s*(?<unit>" + Unit + @")\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex UnsupportedPrefix = new(@"\d+\s*(?:年|个月|月|周|星期|[a-z]+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool TryFind(string text, out TimeSpan duration, out string expression)
    {
        duration = default;
        expression = string.Empty;
        var normalized = MemoReminderRules.NormalizeChineseNumbers(text);
        var matches = Expression.Matches(normalized);
        if (matches.Count != 1) return false;
        var match = matches[0];
        if (UnsupportedPrefix.IsMatch(normalized[..match.Index])) return false;
        var value = match.Groups["duration"].Value;
        long ticks = 0;
        var consumed = 0;
        long previousUnit = long.MaxValue;
        foreach (Match part in Component.Matches(value))
        {
            if (part.Index != consumed || !long.TryParse(part.Groups["n"].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var amount)) return false;
            consumed += part.Length;
            var unit = part.Groups["unit"].Value.ToLowerInvariant();
            var unitTicks = unit switch
            {
                "秒" or "秒钟" or "s" or "sec" or "secs" or "second" or "seconds" => TimeSpan.TicksPerSecond,
                "分" or "分钟" or "m" or "min" or "mins" or "minute" or "minutes" => TimeSpan.TicksPerMinute,
                "小时" or "钟头" or "h" or "hr" or "hrs" or "hour" or "hours" => TimeSpan.TicksPerHour,
                _ => TimeSpan.TicksPerDay,
            };
            if (unitTicks >= previousUnit) return false;
            previousUnit = unitTicks;
            try { ticks = checked(ticks + checked(amount * unitTicks)); }
            catch (OverflowException) { return false; }
        }
        if (consumed != value.Length || ticks <= 0) return false;
        duration = TimeSpan.FromTicks(ticks);
        expression = match.Value;
        return true;
    }
}
