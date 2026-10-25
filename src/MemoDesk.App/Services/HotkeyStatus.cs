using CommunityToolkit.Mvvm.ComponentModel;

namespace MemoDesk.App.Services;

public enum HotkeyBindingState
{
    Idle = 0,
    Recording = 1,
    Applying = 2,
    Active = 3,
    Failed = 4,
}

/// <summary>全局可见的快捷键状态：所有提示都读取同一个实际注册结果。</summary>
public sealed class HotkeyStatus : ObservableObject
{
    private string _configuredGesture = string.Empty;
    private string _effectiveGesture = string.Empty;
    private bool _isRegistered;
    private bool _isRecording;
    private HotkeyBindingState _state = HotkeyBindingState.Idle;
    private string? _error;

    public string ConfiguredGesture
    {
        get => _configuredGesture;
        private set
        {
            if (SetProperty(ref _configuredGesture, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(HintText));
            }
        }
    }

    public string EffectiveGesture
    {
        get => _effectiveGesture;
        private set
        {
            if (SetProperty(ref _effectiveGesture, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(HintText));
            }
        }
    }

    public bool IsRegistered
    {
        get => _isRegistered;
        private set
        {
            if (SetProperty(ref _isRegistered, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(HintText));
            }
        }
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetProperty(ref _isRecording, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(HintText));
            }
        }
    }

    public HotkeyBindingState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(HintText));
            }
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HintText));
            }
        }
    }

    public string DisplayText => State == HotkeyBindingState.Recording
        ? "请按下新的组合键…"
        : IsRegistered
            ? EffectiveGesture
            : "快捷键不可用";

    public string HintText => string.IsNullOrWhiteSpace(Error)
        ? (IsRegistered ? $"当前快捷键：{EffectiveGesture}" : "快捷键不可用，可点击“记一条”或从托盘打开。")
        : $"快捷键不可用：{Error}";

    public void SetConfigured(string gesture, bool registered, string? error)
    {
        ConfiguredGesture = gesture;
        EffectiveGesture = registered ? gesture : string.Empty;
        IsRegistered = registered;
        State = registered ? HotkeyBindingState.Active : HotkeyBindingState.Failed;
        Error = registered ? null : error;
        IsRecording = false;
    }

    public void BeginRecording(string currentGesture)
    {
        ConfiguredGesture = currentGesture;
        State = HotkeyBindingState.Recording;
        IsRecording = true;
        Error = null;
    }

    public void BeginApplying(string candidate)
    {
        EffectiveGesture = candidate;
        State = HotkeyBindingState.Applying;
        IsRecording = false;
        Error = null;
    }

    public void EndRecording(string gesture, bool registered, string? error)
    {
        SetConfigured(gesture, registered, error);
    }

    public void CancelRecording()
    {
        State = IsRegistered ? HotkeyBindingState.Active : HotkeyBindingState.Failed;
        IsRecording = false;
        Error = null;
    }
}
