using System.Globalization;

namespace MemoDesk.Infrastructure.Storage;

/// <summary>SQLite 文本读写约定：时刻 UTC ISO 8601（"O"），日期 yyyy-MM-dd，时间 HH:mm:ss。</summary>
internal static class SqliteMapping
{
    public static string ToUtcText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static object ToDb(DateTimeOffset value) => ToUtcText(value);

    public static object ToDb(DateTimeOffset? value) =>
        value is null ? DBNull.Value : ToUtcText(value.Value);

    public static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTimeOffset? ParseUtcOrNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : ParseUtc(value);

    public static object ToDb(DateOnly? value) =>
        value is null ? DBNull.Value : value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static object ToDb(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly ParseDate(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly? ParseDateOrNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : ParseDate(value);

    public static object ToDb(TimeOnly? value) =>
        value is null ? DBNull.Value : value.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static TimeOnly? ParseTimeOrNull(string? value) =>
        string.IsNullOrEmpty(value)
            ? null
            : TimeOnly.ParseExact(value, "HH:mm:ss", CultureInfo.InvariantCulture);
}
