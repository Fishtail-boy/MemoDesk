using MemoDesk.Infrastructure.Settings;
using MemoDesk.Platform.Windows.Hotkeys;

namespace MemoDesk.App.Services;

/// <summary>
/// 全局快捷键的注册、候选注册与录制协调。
/// 录制成功后先注册候选，再持久化，最后释放旧注册；任一步失败都保留旧键。
/// </summary>
public sealed class HotkeyManager
{
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly AppSettings _settings;
    private int? _registrationId;
    private bool _recording;

    public HotkeyManager(GlobalHotkeyService hotkeyService, AppSettings settings, HotkeyStatus status)
    {
        _hotkeyService = hotkeyService ?? throw new ArgumentNullException(nameof(hotkeyService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Status = status ?? throw new ArgumentNullException(nameof(status));
    }

    public event Action? CaptureRequested;

    /// <summary>录制期间按到当前已注册组合时触发；WPF 可能收不到该组合的普通按键事件。</summary>
    public event Action<string>? RecordingHotkeyPressed;

    public HotkeyStatus Status { get; }

    public string CurrentGesture => _settings.Hotkey;

    /// <summary>启动时按设置注册；失败返回错误信息（不抛出），界面读取 Status 展示。</summary>
    public string? Start()
    {
        var configured = _settings.Hotkey;
        try
        {
            _registrationId = Register(configured);
            Status.SetConfigured(configured, registered: true, error: null);
            return null;
        }
        catch (HotkeyRegistrationException ex)
        {
            _registrationId = null;
            Status.SetConfigured(configured, registered: false, error: ex.Message);
            return ex.Message;
        }
    }

    /// <summary>兼容旧调用：直接应用组合；失败抛出。</summary>
    public void Apply(string gesture)
    {
        var error = ApplyCandidate(gesture);
        if (error is not null)
        {
            throw new HotkeyRegistrationException(error);
        }
    }

    public void BeginRecording()
    {
        if (_recording)
        {
            return;
        }

        _recording = true;
        Status.BeginRecording(_settings.Hotkey);
    }

    public void CancelRecording()
    {
        if (!_recording)
        {
            return;
        }

        _recording = false;
        Status.CancelRecording();
    }

    /// <summary>
    /// 尝试应用录制结果。返回 null 表示成功；否则返回原因并保留旧注册。
    /// 候选注册成功后写设置，写失败会撤销候选注册。
    /// </summary>
    public string? ApplyCandidate(string gesture)
    {
        var previous = _settings.Hotkey;
        var hadPrevious = _registrationId is not null;
        Status.BeginApplying(gesture);

        if (string.Equals(gesture, previous, StringComparison.OrdinalIgnoreCase) && hadPrevious)
        {
            _recording = false;
            Status.EndRecording(previous, registered: true, error: null);
            return null;
        }

        int candidateId;
        try
        {
            candidateId = Register(gesture);
        }
        catch (HotkeyRegistrationException ex)
        {
            _recording = false;
            Status.SetConfigured(previous, hadPrevious, null);
            return ex.Message;
        }

        try
        {
            _settings.Hotkey = gesture;
        }
        catch (Exception ex)
        {
            _hotkeyService.Unregister(candidateId);
            _recording = false;
            Status.SetConfigured(previous, hadPrevious, null);
            return $"保存快捷键失败：{ex.Message}";
        }

        if (_registrationId is { } oldId)
        {
            _hotkeyService.Unregister(oldId);
        }

        _registrationId = candidateId;
        _recording = false;
        Status.EndRecording(gesture, registered: true, error: null);
        return null;
    }

    private int Register(string gesture)
    {
        return _hotkeyService.Register(gesture, OnHotkeyPressed);
    }

    private void OnHotkeyPressed()
    {
        if (_recording)
        {
            RecordingHotkeyPressed?.Invoke(_settings.Hotkey);
            return;
        }

        CaptureRequested?.Invoke();
    }
}
