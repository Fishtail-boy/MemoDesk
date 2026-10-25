namespace MemoDesk.Core;

/// <summary>
/// 应用级默认值。业务规则常量应放在对应领域类型旁边，这里只放进程级默认配置。
/// </summary>
public static class AppDefaults
{
    /// <summary>产品显示名。</summary>
    public const string ProductName = "备忘助手";

    /// <summary>默认业务时区（IANA 标识；.NET 在 Windows 上通过 ICU 支持 IANA 标识）。</summary>
    public const string BusinessTimeZoneId = "Asia/Shanghai";

    /// <summary>快速记录的默认全局快捷键。</summary>
    public const string QuickCaptureHotkey = "Ctrl+Alt+Space";

    public static TimeZoneInfo CreateBusinessTimeZone() =>
        TimeZoneInfo.FindSystemTimeZoneById(BusinessTimeZoneId);
}
