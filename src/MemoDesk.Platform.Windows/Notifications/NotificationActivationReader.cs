using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using MemoDesk.Core.Reminders;

namespace MemoDesk.Platform.Windows.Notifications;

/// <summary>读取冷启动时的通知激活参数；非通知激活返回 null。</summary>
public static class NotificationActivationReader
{
    public static ReminderNotificationAction? TryRead()
    {
        try
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind != ExtendedActivationKind.AppNotification
                || activation.Data is not AppNotificationActivatedEventArgs args)
            {
                return null;
            }

            if (!args.Arguments.TryGetValue("occurrenceId", out var occurrenceText)
                || !Guid.TryParseExact(occurrenceText, "N", out var occurrenceId))
            {
                return null;
            }

            args.Arguments.TryGetValue("action", out var action);
            string? optionId = null;
            if (args.UserInput is { } input && input.TryGetValue("snooze", out var selected))
            {
                optionId = selected;
            }

            return new ReminderNotificationAction(
                occurrenceId,
                string.IsNullOrWhiteSpace(action) ? "open" : action!,
                optionId);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
