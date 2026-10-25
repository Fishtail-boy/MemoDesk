using MemoDesk.Core;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Settings;
using MemoDesk.Core.Time;

namespace MemoDesk.Infrastructure.Settings;

public sealed record WidgetState(bool Visible, bool Pinned, double? Left, double? Top, double? Width, double? Height,
    bool Locked = false, bool Collapsed = false, bool TodayOnly = false);

/// <summary>全局设置的强类型访问（设计 7.7）。API Key 只以 DPAPI 保护后的形式落库。</summary>
public sealed class AppSettings
{
    public const string KeyHotkey = "hotkey";
    public const string KeyTimeZone = "time_zone";
    public const string KeyApiBaseUrl = "deepseek_base_url";
    public const string KeyApiModel = "deepseek_model";
    public const string KeyApiKey = "deepseek_api_key_protected";
    public const string KeySnoozeOptions = "snooze_options";
    public const string KeyUseSystemNotifications = "use_system_notifications";
    public const string KeyAllowFallbackNotifications = "allow_fallback_notifications";
    public const string KeyWidget = "memo_widget";

    public const string DefaultApiBaseUrl = "https://api.deepseek.com";
    public const string DefaultApiModel = "deepseek-flash";

    private readonly ISettingsStore _store;
    private readonly ISecretProtector _protector;

    public AppSettings(ISettingsStore store, ISecretProtector protector)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public string TimeZoneId
    {
        get => GetString(KeyTimeZone) ?? AppDefaults.BusinessTimeZoneId;
        set => SetString(KeyTimeZone, value);
    }

    public TimeZoneInfo TimeZone
    {
        get
        {
            try
            {
                return BusinessTime.Resolve(TimeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
                return AppDefaults.CreateBusinessTimeZone();
            }
            catch (InvalidTimeZoneException)
            {
                return AppDefaults.CreateBusinessTimeZone();
            }
        }
    }

    public string Hotkey
    {
        get => GetString(KeyHotkey) ?? AppDefaults.QuickCaptureHotkey;
        set => SetString(KeyHotkey, value);
    }

    public string ApiBaseUrl
    {
        get => GetString(KeyApiBaseUrl) ?? DefaultApiBaseUrl;
        set => SetString(KeyApiBaseUrl, value);
    }

    public string ApiModel
    {
        get => GetString(KeyApiModel) ?? DefaultApiModel;
        set => SetString(KeyApiModel, value);
    }

    public string? ApiKey
    {
        get
        {
            var stored = GetString(KeyApiKey);
            if (string.IsNullOrEmpty(stored))
            {
                return null;
            }

            try
            {
                return _protector.Unprotect(stored);
            }
            catch (Exception)
            {
                // 保护数据损坏（例如换了 Windows 用户）：视为未配置，要求重新填写。
                return null;
            }
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _store.Remove(KeyApiKey);
                return;
            }

            _store.Set(KeyApiKey, _protector.Protect(value.Trim()));
        }
    }

    public bool UseSystemNotifications
    {
        get => GetString(KeyUseSystemNotifications) is not { } raw
            || !bool.TryParse(raw, out var parsed)
            || parsed;
        set => SetString(KeyUseSystemNotifications, value ? "true" : "false");
    }

    public bool AllowFallbackNotifications
    {
        get => !bool.TryParse(GetString(KeyAllowFallbackNotifications), out var enabled) || enabled;
        set => SetString(KeyAllowFallbackNotifications, value ? "true" : "false");
    }

    public IReadOnlyList<SnoozeOption> SnoozeOptions
    {
        get
        {
            var raw = GetString(KeySnoozeOptions);
            if (string.IsNullOrEmpty(raw))
            {
                return SnoozeOption.Defaults;
            }

            try
            {
                var parsed = JsonDefaults.Deserialize<List<SnoozeOption>>(raw);
                if (parsed is { Count: > 0 } && parsed.All(o => !string.IsNullOrWhiteSpace(o.Id) && !string.IsNullOrWhiteSpace(o.Label)))
                {
                    return parsed;
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }

            return SnoozeOption.Defaults;
        }
        set => SetString(KeySnoozeOptions, JsonDefaults.Serialize(value));
    }

    public WidgetState Widget
    {
        get
        {
            var raw = GetString(KeyWidget);
            if (!string.IsNullOrEmpty(raw))
            {
                try
                {
                    var parsed = JsonDefaults.Deserialize<WidgetState>(raw);
                    if (parsed is not null)
                    {
                        return parsed;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                }
            }

            return new WidgetState(Visible: true, Pinned: false, Left: null, Top: null, Width: 300, Height: 260);
        }
        set => SetString(KeyWidget, JsonDefaults.Serialize(value));
    }

    private string? GetString(string key)
    {
        var value = _store.Get(key);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private void SetString(string key, string value) => _store.Set(key, value);
}
