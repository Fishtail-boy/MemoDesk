using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using MemoDesk.Core.Reminders;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace MemoDesk.Platform.Windows.Notifications;

/// <summary>
/// Windows 通知网关：即时通知走 Windows App SDK AppNotificationManager，
/// 计划通知走 ScheduledToastNotification（AppNotificationManager 没有 Schedule API）。
/// 即时通知支持 SDK 注册的打包/未打包应用；系统计划需要可用的应用身份。
/// 初始化或明确投递失败时，由调用方按设置回落应用内提醒。
/// </summary>
public sealed class WindowsNotificationGateway : IReminderNotificationGateway, IDisposable
{
    private const string GroupName = "MemoDesk";
    private AppNotificationManager? _manager;
    private bool _registered;
    private bool _disposed;
    private string? _lastError;

    public bool IsAvailable { get; private set; }

    public bool IsSchedulingSupported { get; private set; }

    /// <summary>静默动作模式退出时保留刚登记的系统计划通知，不主动注销通知平台。</summary>
    public bool PreserveScheduledOnDispose { get; set; }

    public event Action<ReminderNotificationAction>? ActionInvoked;

    /// <summary>在 UI 线程调用；返回失败原因（已自动回退为不可用）。</summary>
    public string? Initialize(bool replayActivation = true)
    {
        try
        {
            _manager = AppNotificationManager.Default;
            // 必须先订阅再 Register：否则运行中的应用可能被忽略，系统会启动新进程处理激活。
            _manager.NotificationInvoked += OnNotificationInvoked;
            _manager.Register();
            _registered = true;
            IsAvailable = true;

            // 冷启动：由计划通知/按钮拉起应用时，从激活参数补处理一次。
            if (replayActivation)
            {
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                if (activation.Kind == ExtendedActivationKind.AppNotification
                    && activation.Data is AppNotificationActivatedEventArgs activated)
                {
                    OnNotificationInvoked(_manager, activated);
                }
            }
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            _lastError = ex.Message;
            return ex.Message;
        }

        try
        {
            _ = ToastNotificationManager.CreateToastNotifier();
            IsSchedulingSupported = true;
        }
        catch (Exception)
        {
            IsSchedulingSupported = false;
        }

        return null;
    }

    public NotificationDeliveryOutcome SendNow(ReminderNotification notification)
    {
        if (!IsAvailable || _manager is null || !NotificationsEnabled())
        {
            return NotificationDeliveryOutcome.DefiniteFailure;
        }

        try
        {
            var builder = BuildContent(
                notification.OccurrenceId,
                notification.Title,
                notification.Body,
                notification.TimeText,
                notification.IsCatchUp,
                notification.Options);

            var payload = builder.BuildNotification();
            payload.Tag = notification.Tag;
            payload.Group = GroupName;
            _manager.Show(payload);
            return NotificationDeliveryOutcome.HandedOff;
        }
        catch (ArgumentException ex)
        {
            _lastError = ex.Message;
            return NotificationDeliveryOutcome.DefiniteFailure;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            return NotificationDeliveryOutcome.Uncertain;
        }
    }

    public string DescribeStatus()
    {
        if (!IsAvailable || _manager is null) return "系统通知未就绪：" + (_lastError ?? "尚未完成注册");
        try
        {
            var state = _manager.Setting;
            return state == AppNotificationSetting.Enabled
                ? $"Windows 通知已就绪 · {(IsSchedulingSupported ? "系统计划可用" : "由应用负责计时")}" + (_lastError is null ? string.Empty : $"；上次调用：{_lastError}")
                : $"Windows 已关闭或限制此应用通知（{state}），可在系统通知设置中开启。";
        }
        catch (Exception ex) { return "无法读取通知状态：" + ex.Message; }
    }
    private bool NotificationsEnabled()
    {
        try { return _manager?.Setting == AppNotificationSetting.Enabled; }
        catch (Exception ex) { _lastError = ex.Message; return false; }
    }
    public NotificationDeliveryOutcome SendTest()
    {
        if (!IsAvailable || _manager is null || !NotificationsEnabled()) return NotificationDeliveryOutcome.DefiniteFailure;
        try
        {
            _manager.Show(new AppNotificationBuilder().AddText("备忘助手 · 测试通知")
                .AddText("系统通知已接收测试请求。正式提醒可在此完成或选择稍后提醒。")
                .BuildNotification());
            return NotificationDeliveryOutcome.HandedOff;
        }
        catch (Exception ex) { _lastError = ex.Message; return NotificationDeliveryOutcome.Uncertain; }
    }

    public NotificationScheduleOutcome Schedule(ScheduledReminder reminder)
    {
        if (!IsSchedulingSupported || !NotificationsEnabled())
        {
            return NotificationScheduleOutcome.Unsupported;
        }

        try
        {
            var builder = BuildContent(
                reminder.OccurrenceId,
                reminder.Title,
                reminder.Body,
                reminder.TimeText,
                isCatchUp: false,
                reminder.Options);

            var payload = builder.BuildNotification().Payload;
            var xml = new XmlDocument();
            xml.LoadXml(payload);

            var scheduled = new ScheduledToastNotification(xml, reminder.DeliverAtUtc.ToUniversalTime())
            {
                Tag = reminder.Tag,
                Group = GroupName,
            };

            ToastNotificationManager.CreateToastNotifier().AddToSchedule(scheduled);
            return NotificationScheduleOutcome.Scheduled;
        }
        catch (Exception)
        {
            return NotificationScheduleOutcome.DefiniteFailure;
        }
    }

    public bool CancelScheduled(string notificationTag)
    {
        if (!IsSchedulingSupported)
        {
            // 当前环境没有系统计划通道，视为无需撤销。
            return true;
        }

        try
        {
            var notifier = ToastNotificationManager.CreateToastNotifier();
            foreach (var scheduled in notifier.GetScheduledToastNotifications()
                .Where(item => string.Equals(item.Tag, notificationTag, StringComparison.Ordinal))
                .ToArray())
            {
                notifier.RemoveFromSchedule(scheduled);
            }

            return true;
        }
        catch (Exception)
        {
            // 清理失败由数据库中的撤销任务在下次核对时重试。
            return false;
        }
    }

    public NotificationScheduleSnapshot QueryScheduled()
    {
        if (!IsSchedulingSupported)
        {
            return new NotificationScheduleSnapshot(false, Array.Empty<string>(), "系统计划通知不可用。");
        }

        try
        {
            var tags = ToastNotificationManager.CreateToastNotifier()
                .GetScheduledToastNotifications()
                .Where(item => string.Equals(item.Group, GroupName, StringComparison.Ordinal))
                .Select(item => item.Tag)
                .ToArray();
            return new NotificationScheduleSnapshot(true, tags, null);
        }
        catch (Exception ex)
        {
            return new NotificationScheduleSnapshot(false, Array.Empty<string>(), ex.Message);
        }
    }

    public NotificationHistorySnapshot QueryHistory()
    {
        try
        {
            var tags = ToastNotificationManager.History.GetHistory()
                .Where(item => string.Equals(item.Group, GroupName, StringComparison.Ordinal))
                .Select(item => item.Tag)
                .Where(tag => !string.IsNullOrEmpty(tag))
                .ToArray();
            return new NotificationHistorySnapshot(true, tags, null);
        }
        catch (Exception ex)
        {
            return new NotificationHistorySnapshot(false, Array.Empty<string>(), ex.Message);
        }
    }

    private static AppNotificationBuilder BuildContent(
        Guid occurrenceId,
        string title,
        string body,
        string timeText,
        bool isCatchUp,
        IReadOnlyList<SnoozeOption> options)
    {
        var builder = new AppNotificationBuilder()
            .AddArgument("occurrenceId", occurrenceId.ToString("N"))
            .AddText(title)
            .AddText(body);

        if (!string.IsNullOrWhiteSpace(timeText))
        {
            builder.AddText(timeText);
        }

        if (isCatchUp)
        {
            builder.AddText("补提醒");
        }

        if (options.Count > 0)
        {
            var combo = new AppNotificationComboBox("snooze");
            foreach (var option in options.Take(5))
            {
                combo.AddItem(option.Id, option.Label);
            }

            combo.SetSelectedItem(options[0].Id);
            builder.AddComboBox(combo);
        }

        builder.AddButton(new AppNotificationButton("已完成")
            .AddArgument("action", "complete")
            .AddArgument("occurrenceId", occurrenceId.ToString("N")));

        builder.AddButton(new AppNotificationButton("稍后提醒")
            .AddArgument("action", "snooze")
            .AddArgument("occurrenceId", occurrenceId.ToString("N")));

        return builder;
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            if (!args.Arguments.TryGetValue("occurrenceId", out var occurrenceText)
                || !Guid.TryParseExact(occurrenceText, "N", out var occurrenceId))
            {
                return;
            }

            args.Arguments.TryGetValue("action", out var action);
            string? optionId = null;
            if (args.UserInput is { } input && input.TryGetValue("snooze", out var selected))
            {
                optionId = selected;
            }

            ActionInvoked?.Invoke(new ReminderNotificationAction(
                occurrenceId,
                string.IsNullOrWhiteSpace(action) ? "open" : action!,
                optionId));
        }
        catch (Exception)
        {
            // 激活参数异常不应影响进程。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_manager is not null)
        {
            try
            {
                _manager.NotificationInvoked -= OnNotificationInvoked;
                if (_registered && !PreserveScheduledOnDispose)
                {
                    _manager.Unregister();
                }
            }
            catch (Exception)
            {
                // 退出清理失败不影响进程结束。
            }
        }

        _registered = false;
        IsAvailable = false;
    }
}
