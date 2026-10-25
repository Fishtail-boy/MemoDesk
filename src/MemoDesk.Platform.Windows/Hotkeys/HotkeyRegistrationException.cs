namespace MemoDesk.Platform.Windows.Hotkeys;

/// <summary>全局快捷键注册失败（被占用、格式无效等）。消息面向用户。</summary>
public sealed class HotkeyRegistrationException : Exception
{
    public HotkeyRegistrationException(string message)
        : base(message)
    {
    }
}
