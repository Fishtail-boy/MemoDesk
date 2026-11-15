using MemoDesk.Core.Time;

namespace MemoDesk.Core.Domain;

public enum AssetExpirationStatus
{
    NoDate,
    Active,
    DueSoon,
    Expired,
}

public sealed record DigitalAsset(
    Guid Id,
    string Name,
    string Category,
    Uri? Link,
    string Description,
    DateOnly? ExpirationDate,
    TimeOnly? ExpirationTime)
{
    public const int DueSoonDays = 30;

    public AssetExpirationStatus GetExpirationStatus(DateTimeOffset now, TimeZoneInfo timeZone)
    {
        if (ExpirationDate is not { } date)
            return AssetExpirationStatus.NoDate;

        var days = date.DayNumber - BusinessTime.Today(now, timeZone).DayNumber;
        if (days < 0 || (days == 0 && ExpirationTime is { } time && now >= BusinessTime.Combine(date, time, timeZone)))
            return AssetExpirationStatus.Expired;

        return days <= DueSoonDays ? AssetExpirationStatus.DueSoon : AssetExpirationStatus.Active;
    }

    public static bool TryParseLink(string text, out Uri? link)
    {
        link = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") || string.IsNullOrEmpty(parsed.Host))
            return false;

        link = parsed;
        return true;
    }
}
