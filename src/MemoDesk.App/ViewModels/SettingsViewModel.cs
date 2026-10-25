using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MemoDesk.App.Services;
using MemoDesk.Core;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Time;
using MemoDesk.Infrastructure.Settings;
using MemoDesk.Platform.Windows.Notifications;

namespace MemoDesk.App.ViewModels;

public sealed record SnoozeKindOption(SnoozeKind Kind, string Label);

/// <summary>可编辑的稍后提醒选项（1–5 项，受 Toast 选择框上限约束）。</summary>
public partial class SnoozeOptionEditViewModel : ObservableObject
{
    public SnoozeOptionEditViewModel(SnoozeOption option)
    {
        Id = option.Id;
        Label = option.Label;
        Kind = option.Kind;
        Minutes = option.Minutes;
        TimeText = option.TomorrowTime is { } time ? time.ToString(time.Second == 0 ? "HH:mm" : "HH:mm:ss") : "09:00";
    }

    public string Id { get; }

    [ObservableProperty]
    private string label;

    [ObservableProperty]
    private SnoozeKind kind;

    [ObservableProperty]
    private int minutes;

    [ObservableProperty]
    private string timeText;

    public bool IsRelative => Kind == SnoozeKind.Relative;

    public bool IsTomorrow => Kind == SnoozeKind.TomorrowAt;

    partial void OnKindChanged(SnoozeKind value)
    {
        OnPropertyChanged(nameof(IsRelative));
        OnPropertyChanged(nameof(IsTomorrow));
    }
}

/// <summary>设置（设计 7.7）：快捷键、时区、DeepSeek 配置、稍后提醒选项与 API Key 管理。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly HotkeyManager _hotkeys;
    private readonly WindowsNotificationGateway? _notifications;

    public SettingsViewModel(AppSettings settings, HotkeyManager hotkeys, WindowsNotificationGateway? notifications = null)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _notifications = notifications;

        _hotkeys.Status.PropertyChanged += (_, _) => ApplyHotkeyStatus();
        _hotkeys.RecordingHotkeyPressed += gesture =>
        {
            if (HotkeyIsRecording)
            {
                // 当前组合可能被全局热键抢走普通键盘事件；直接按“保持当前组合”结束录制。
                CommitHotkeyRecording(gesture);
            }
        };

        TimeZoneOptions = new[]
        {
            "Asia/Shanghai", "Asia/Hong_Kong", "Asia/Taipei", "Asia/Tokyo",
            "Asia/Singapore", "Europe/London", "America/New_York", "UTC",
        };

        Load();
    }

    public IReadOnlyList<string> TimeZoneOptions { get; }

    public IReadOnlyList<SnoozeKindOption> SnoozeKinds { get; } = new[]
    {
        new SnoozeKindOption(SnoozeKind.Relative, "相对时长"),
        new SnoozeKindOption(SnoozeKind.TomorrowAt, "明天时刻"),
    };

    public ObservableCollection<SnoozeOptionEditViewModel> SnoozeOptions { get; } = new();

    /// <summary>设置保存完成后触发，供通知协调器立即重新对账。</summary>
    public event Action? SettingsSaved;

    [ObservableProperty]
    private string hotkeyText = string.Empty;

    [ObservableProperty]
    private string hotkeyHint = string.Empty;

    [ObservableProperty]
    private bool hotkeyIsRecording;

    [ObservableProperty]
    private string timeZoneId = string.Empty;

    [ObservableProperty]
    private string apiBaseUrl = string.Empty;

    [ObservableProperty]
    private string apiModel = string.Empty;

    [ObservableProperty]
    private string apiKeyInput = string.Empty;

    [ObservableProperty]
    private bool hasApiKey;

    [ObservableProperty]
    private bool useSystemNotifications;

    [ObservableProperty] private bool allowFallbackNotifications;
    [ObservableProperty] private bool showDesktopWidget;
    [ObservableProperty] private string notificationStatus = string.Empty;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    public void Load()
    {
        HotkeyText = _hotkeys.Status.DisplayText;
        HotkeyHint = _hotkeys.Status.HintText;
        HotkeyIsRecording = false;
        TimeZoneId = _settings.TimeZoneId;
        ApiBaseUrl = _settings.ApiBaseUrl;
        ApiModel = _settings.ApiModel;
        HasApiKey = !string.IsNullOrEmpty(_settings.ApiKey);
        UseSystemNotifications = _settings.UseSystemNotifications;
        AllowFallbackNotifications = _settings.AllowFallbackNotifications;
        ShowDesktopWidget = _settings.Widget.Visible;
        RefreshNotificationStatus();
        ApiKeyInput = string.Empty;

        SnoozeOptions.Clear();
        foreach (var option in _settings.SnoozeOptions)
        {
            SnoozeOptions.Add(new SnoozeOptionEditViewModel(option));
        }

        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private void BeginHotkeyRecording()
    {
        _hotkeys.BeginRecording();
        HotkeyText = "请按下新的组合键…";
        HotkeyHint = "至少一个修饰键 + 一个主键；松开后生效，Esc 取消。";
        HotkeyIsRecording = true;
    }

    [RelayCommand]
    private void CancelHotkeyRecording()
    {
        if (!HotkeyIsRecording)
        {
            return;
        }

        _hotkeys.CancelRecording();
        HotkeyIsRecording = false;
        ApplyHotkeyStatus();
    }

    [RelayCommand]
    private void ResetHotkey()
    {
        var error = _hotkeys.ApplyCandidate(AppDefaults.QuickCaptureHotkey);
        StatusMessage = error ?? $"已恢复默认快捷键：{AppDefaults.QuickCaptureHotkey}";
        HotkeyIsRecording = false;
        ApplyHotkeyStatus();
    }

    /// <summary>由录制控件在全部按键松开后调用。</summary>
    public void CommitHotkeyRecording(string gesture)
    {
        if (!HotkeyIsRecording)
        {
            return;
        }

        var error = _hotkeys.ApplyCandidate(gesture);
        HotkeyIsRecording = false;
        StatusMessage = error ?? $"快捷键已更新：{gesture}";
        ApplyHotkeyStatus();
    }

    public void ShowHotkeyCandidate(string gesture)
    {
        if (HotkeyIsRecording)
        {
            HotkeyText = gesture;
        }
    }

    private void ApplyHotkeyStatus()
    {
        HotkeyText = _hotkeys.Status.DisplayText;
        HotkeyHint = _hotkeys.Status.HintText;
    }

    [RelayCommand]
    private void AddSnoozeOption()
    {
        if (SnoozeOptions.Count >= 5)
        {
            StatusMessage = "稍后提醒选项最多 5 项（系统通知选择框上限）。";
            return;
        }

        SnoozeOptions.Add(new SnoozeOptionEditViewModel(new SnoozeOption(
            $"custom-{Guid.NewGuid():N}", "自定义", SnoozeKind.Relative, 15, null)));
    }

    [RelayCommand]
    private void RemoveSnoozeOption(SnoozeOptionEditViewModel option)
    {
        if (SnoozeOptions.Count <= 1)
        {
            StatusMessage = "至少保留 1 个稍后提醒选项。";
            return;
        }

        SnoozeOptions.Remove(option);
    }

    [RelayCommand]
    private void Save()
    {
        if (!TryBuildSnoozeOptions(out var options, out var error))
        {
            StatusMessage = $"稍后提醒选项未保存：{error}";
            return;
        }

        if (!string.IsNullOrWhiteSpace(TimeZoneId))
        {
            try
            {
                var zone = BusinessTime.Resolve(TimeZoneId.Trim());
                _settings.TimeZoneId = zone.Id;
            }
            catch (Exception)
            {
                TimeZoneId = _settings.TimeZoneId;
                StatusMessage = "时区无效，未保存时区设置。";
            }
        }

        if (!string.IsNullOrWhiteSpace(ApiBaseUrl))
        {
            _settings.ApiBaseUrl = ApiBaseUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(ApiModel))
        {
            _settings.ApiModel = ApiModel.Trim();
        }

        if (!string.IsNullOrWhiteSpace(ApiKeyInput))
        {
            _settings.ApiKey = ApiKeyInput;
            ApiKeyInput = string.Empty;
            HasApiKey = true;
        }

        _settings.SnoozeOptions = options;
        _settings.UseSystemNotifications = UseSystemNotifications;
        _settings.AllowFallbackNotifications = AllowFallbackNotifications;
        _settings.Widget = _settings.Widget with { Visible = ShowDesktopWidget };

        if (string.IsNullOrEmpty(StatusMessage))
        {
            StatusMessage = "设置已保存。";
        }

        SettingsSaved?.Invoke();
    }

    [RelayCommand]
    private void RefreshNotificationStatus() => NotificationStatus = _notifications?.DescribeStatus() ?? "通知状态将在正式应用中显示。";

    [RelayCommand]
    private void TestNotification()
    {
        var outcome = _notifications?.SendTest();
        RefreshNotificationStatus();
        StatusMessage = outcome == Core.Reminders.NotificationDeliveryOutcome.HandedOff
            ? "测试请求已交给 Windows；若没有横幅，请检查系统通知和勿扰设置。"
            : "测试通知未确认发送，请查看通知状态；本次不使用兜底卡片替代测试。";
    }

    [RelayCommand]
    private void OpenNotificationSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true }); }
        catch (Exception ex) { StatusMessage = "无法打开系统通知设置：" + ex.Message; }
    }

    [RelayCommand]
    private void ClearApiKey()
    {
        _settings.ApiKey = null;
        HasApiKey = false;
        ApiKeyInput = string.Empty;
        StatusMessage = "API Key 已清除。";
    }

    [RelayCommand]
    private void ResetSnoozeOptions()
    {
        _settings.SnoozeOptions = SnoozeOption.Defaults;
        SnoozeOptions.Clear();
        foreach (var option in SnoozeOption.Defaults)
        {
            SnoozeOptions.Add(new SnoozeOptionEditViewModel(option));
        }

        StatusMessage = "稍后提醒选项已恢复默认。";
    }

    private bool TryBuildSnoozeOptions(out IReadOnlyList<SnoozeOption> options, out string error)
    {
        options = Array.Empty<SnoozeOption>();
        error = string.Empty;

        if (SnoozeOptions.Count is < 1 or > 5)
        {
            error = "选项数量必须为 1–5 项。";
            return false;
        }

        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SnoozeOption>();

        foreach (var edit in SnoozeOptions)
        {
            var label = edit.Label.Trim();
            if (label.Length == 0)
            {
                error = "选项名称不能为空。";
                return false;
            }

            if (!labels.Add(label))
            {
                error = $"选项名称重复：{label}。";
                return false;
            }

            if (edit.Kind == SnoozeKind.Relative)
            {
                if (edit.Minutes is < 1 or > 1440)
                {
                    error = $"“{label}”的分钟数必须在 1–1440 之间。";
                    return false;
                }

                result.Add(new SnoozeOption(edit.Id, label, SnoozeKind.Relative, edit.Minutes, null));
            }
            else
            {
                if (!TimeOnly.TryParse(edit.TimeText, out var time))
                {
                    error = $"“{label}”的时刻格式无效（应为 HH:mm 或 HH:mm:ss）。";
                    return false;
                }

                result.Add(new SnoozeOption(edit.Id, label, SnoozeKind.TomorrowAt, 0, time));
            }
        }

        options = result;
        return true;
    }
}
