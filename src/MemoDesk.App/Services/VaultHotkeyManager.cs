using MemoDesk.Infrastructure.Settings;
using MemoDesk.Platform.Windows.Hotkeys;

namespace MemoDesk.App.Services;

public sealed class VaultHotkeyManager(GlobalHotkeyService service, AppSettings settings) : IDisposable
{
    private int? _id;
    public bool Recording { get; set; }
    public string Gesture => settings.VaultHotkey;
    public string Status { get; private set; } = "";
    public event Action? Requested;
    public string? Apply(string gesture)
    {
        if (_id is not null && gesture == Gesture) return null;
        int candidate;
        try { candidate = service.Register(gesture, () => { if (!Recording) Requested?.Invoke(); }); }
        catch (HotkeyRegistrationException) { Status = "密码库快捷键被占用或无效，可重新绑定。"; return Status; }
        try { settings.VaultHotkey = gesture; }
        catch (Exception) { service.Unregister(candidate); Status = "快捷键设置保存失败。"; return Status; }
        if (_id is { } old) service.Unregister(old);
        _id = candidate; Status = ""; return null;
    }
    public void Dispose() { if (_id is { } id) service.Unregister(id); _id = null; GC.SuppressFinalize(this); }
}
