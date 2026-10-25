using System.Globalization;

namespace MemoDesk.Core.Time;

/// <summary>
/// 业务时间语义（设计 3.2）：相对时间以原文捕获时区和捕获时间为基准；
/// 具体时刻以 UTC 保存；纯日期按业务日期保存，不转换为 UTC 零点。
/// </summary>
public static class BusinessTime
{
    public static TimeZoneInfo Resolve(string timeZoneId) =>
        TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

    /// <summary>UTC 时刻在指定业务时区中的日期。</summary>
    public static DateOnly DateOf(DateTimeOffset utcInstant, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(utcInstant, timeZone);
        return DateOnly.FromDateTime(local.DateTime);
    }

    public static DateOnly Today(DateTimeOffset nowUtc, TimeZoneInfo timeZone) => DateOf(nowUtc, timeZone);

    /// <summary>业务日期的零点（UTC 时刻）。</summary>
    public static DateTimeOffset StartOfDateUtc(DateOnly date, TimeZoneInfo timeZone) =>
        ToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), timeZone);

    /// <summary>业务日期 + 时刻 → UTC。</summary>
    public static DateTimeOffset Combine(DateOnly date, TimeOnly time, TimeZoneInfo timeZone) =>
        ToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), timeZone);

    /// <summary>
    /// 本地时间 → UTC。遇到夏令时空洞（不存在的时间）向后顺延一小时；
    /// 重叠时间按标准时间解释。
    /// </summary>
    public static DateTimeOffset ToUtc(DateTime localDateTime, TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        try
        {
            var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone);
            return new DateTimeOffset(utc, TimeSpan.Zero);
        }
        catch (ArgumentException)
        {
            var adjusted = TimeZoneInfo.ConvertTimeToUtc(unspecified.AddHours(1), timeZone);
            return new DateTimeOffset(adjusted, TimeSpan.Zero);
        }
    }

    /// <summary>下一个自然日的指定时刻（“明天 09:00”语义，不是加 24 小时）。</summary>
    public static DateTimeOffset TomorrowAt(DateTimeOffset nowUtc, TimeZoneInfo timeZone, TimeOnly time)
    {
        var today = DateOf(nowUtc, timeZone);
        return Combine(today.AddDays(1), time, timeZone);
    }

    /// <summary>把 UTC 时刻格式化为业务时区的可读文本。</summary>
    public static string FormatLocal(DateTimeOffset utcInstant, TimeZoneInfo timeZone, string format)
    {
        var local = TimeZoneInfo.ConvertTime(utcInstant, timeZone);
        return local.ToString(format, CultureInfo.InvariantCulture);
    }

    public static DateOnly ParseDate(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
