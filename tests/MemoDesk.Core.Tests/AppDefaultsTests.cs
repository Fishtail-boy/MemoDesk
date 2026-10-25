using MemoDesk.Core;

namespace MemoDesk.Core.Tests;

public sealed class AppDefaultsTests
{
    [Fact]
    public void BusinessTimeZone_ResolvesToUtcPlusEight()
    {
        var zone = AppDefaults.CreateBusinessTimeZone();

        Assert.Equal(TimeSpan.FromHours(8), zone.BaseUtcOffset);
    }

    [Fact]
    public void ProductName_IsNotBlank()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppDefaults.ProductName));
    }
}
