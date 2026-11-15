using MemoDesk.Core.Domain;

namespace MemoDesk.Core.Tests;

public sealed class DigitalAssetTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "Asset test UTC+08", TimeSpan.FromHours(8), "Asset test UTC+08", "Asset test UTC+08");
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 11, 30, 0, TimeSpan.FromHours(8));

    [Theory]
    [InlineData(null, AssetExpirationStatus.NoDate)]
    [InlineData(-1, AssetExpirationStatus.Expired)]
    [InlineData(0, AssetExpirationStatus.DueSoon)]
    [InlineData(30, AssetExpirationStatus.DueSoon)]
    [InlineData(31, AssetExpirationStatus.Active)]
    public void ExpirationStatus_UsesTheBusinessDate(int? daysFromToday, AssetExpirationStatus expected)
    {
        var today = DateOnly.FromDateTime(Now.DateTime);
        var asset = new DigitalAsset(Guid.NewGuid(), "服务器", "云服务", null, "", daysFromToday is { } days ? today.AddDays(days) : null, null);

        Assert.Equal(expected, asset.GetExpirationStatus(Now, Zone));
    }

    [Fact]
    public void ExpirationStatus_ExpiresAtTheSpecifiedLocalTime()
    {
        var asset = new DigitalAsset(Guid.NewGuid(), "会员", "订阅", null, "", DateOnly.FromDateTime(Now.DateTime), new TimeOnly(11, 30));

        Assert.Equal(AssetExpirationStatus.DueSoon, asset.GetExpirationStatus(Now.AddSeconds(-1).ToUniversalTime(), Zone));
        Assert.Equal(AssetExpirationStatus.Expired, asset.GetExpirationStatus(Now.ToUniversalTime(), Zone));
    }

    [Theory]
    [InlineData("https://example.com/member", true)]
    [InlineData("http://localhost:8080/", true)]
    [InlineData("", true)]
    [InlineData("example.com", false)]
    [InlineData("file:///C:/test", false)]
    [InlineData("javascript:alert(1)", false)]
    public void TryParseLink_OnlyAcceptsOptionalWebLinks(string input, bool expected)
    {
        Assert.Equal(expected, DigitalAsset.TryParseLink(input, out var link));
        Assert.Equal(expected && input.Length > 0, link is not null);
    }
}
