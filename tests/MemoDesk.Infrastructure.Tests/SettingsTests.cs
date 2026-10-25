using System.Text;
using MemoDesk.Core.Settings;
using MemoDesk.Infrastructure.Settings;

namespace MemoDesk.Infrastructure.Tests;

internal sealed class Base64Protector : ISecretProtector
{
    public string Protect(string plaintext) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes("protected:" + plaintext));

    public string Unprotect(string protectedValue) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue))["protected:".Length..];
}

public sealed class SettingsTests
{
    [Fact]
    public void AppSettings_UsesDefaultsOnFreshDatabase()
    {
        using var db = new TestDatabase();
        var settings = new AppSettings(db.SettingsStore, new Base64Protector());

        Assert.Equal("Asia/Shanghai", settings.TimeZoneId);
        Assert.Equal("Ctrl+Alt+Space", settings.Hotkey);
        Assert.Equal("https://api.deepseek.com", settings.ApiBaseUrl);
        Assert.Null(settings.ApiKey);
        Assert.Equal(SnoozeOptionDefaults.Count, settings.SnoozeOptions.Count);
        Assert.True(settings.Widget.Visible);
        Assert.True(settings.UseSystemNotifications);
        Assert.True(settings.AllowFallbackNotifications);
    }

    [Fact]
    public void ApiKey_IsStoredProtectedAndRoundTrips()
    {
        using var db = new TestDatabase();
        var settings = new AppSettings(db.SettingsStore, new Base64Protector());

        settings.ApiKey = "sk-secret-123";

        var stored = db.SettingsStore.Get(AppSettings.KeyApiKey);
        Assert.NotNull(stored);
        Assert.DoesNotContain("sk-secret-123", stored);
        Assert.Equal("sk-secret-123", settings.ApiKey);

        settings.ApiKey = null;
        Assert.Null(settings.ApiKey);
        Assert.Null(db.SettingsStore.Get(AppSettings.KeyApiKey));
    }

    [Fact]
    public void SnoozeOptions_InvalidJson_FallsBackToDefaults()
    {
        using var db = new TestDatabase();
        db.SettingsStore.Set(AppSettings.KeySnoozeOptions, "not json");
        var settings = new AppSettings(db.SettingsStore, new Base64Protector());

        Assert.Equal(SnoozeOptionDefaults.Count, settings.SnoozeOptions.Count);
    }

    [Fact]
    public void WidgetState_RoundTrips()
    {
        using var db = new TestDatabase();
        var settings = new AppSettings(db.SettingsStore, new Base64Protector());

        settings.Widget = new WidgetState(true, true, 10.5, 20.5, 320, 420);

        var loaded = settings.Widget;
        Assert.True(loaded.Visible);
        Assert.True(loaded.Pinned);
        Assert.Equal(10.5, loaded.Left);
        Assert.Equal(420, loaded.Height);
    }

    [Fact]
    public void OldWidgetJsonRetainsHiddenPreferenceAndNewFlagsDefaultSafely()
    {
        using var db = new TestDatabase();
        db.SettingsStore.Set(AppSettings.KeyWidget, "{\"Visible\":false,\"Pinned\":true,\"Left\":10,\"Top\":20,\"Width\":300,\"Height\":380}");
        var settings = new AppSettings(db.SettingsStore, new Base64Protector());
        Assert.False(settings.Widget.Visible);
        Assert.True(settings.Widget.Pinned);
        Assert.False(settings.Widget.Locked);
        Assert.False(settings.Widget.Collapsed);
        Assert.False(settings.Widget.TodayOnly);
        settings.Widget = settings.Widget with { Locked = true, Collapsed = true, TodayOnly = true };
        settings.AllowFallbackNotifications = false;
        var reloaded = new AppSettings(db.SettingsStore, new Base64Protector());
        Assert.True(reloaded.Widget.Locked && reloaded.Widget.Collapsed && reloaded.Widget.TodayOnly);
        Assert.False(reloaded.Widget.Visible);
        Assert.False(reloaded.AllowFallbackNotifications);
    }

    [Fact]
    public void TimeZone_FallsBackWhenInvalidIdStored()
    {
        using var db = new TestDatabase();
        db.SettingsStore.Set(AppSettings.KeyTimeZone, "Not/AZone");
        var settings = new AppSettings(db.SettingsStore, new Base64Protector());

        Assert.Equal(TimeSpan.FromHours(8), settings.TimeZone.BaseUtcOffset);
    }
}

internal sealed class SnoozeOptionDefaults
{
    public static int Count => MemoDesk.Core.Reminders.SnoozeOption.Defaults.Count;
}
