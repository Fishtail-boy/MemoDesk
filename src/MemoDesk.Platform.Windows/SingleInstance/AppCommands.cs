using MemoDesk.Core.Reminders;

namespace MemoDesk.Platform.Windows.SingleInstance;

/// <summary>进程间转发的启动命令。</summary>
public static class AppCommands
{
    /// <summary>唤起主窗口（重复启动、托盘左键等场景）。</summary>
    public const string Activate = "activate";

    private const string NotificationActionPrefix = "notify:";

    /// <summary>把通知激活参数编码成可经命名管道转发的命令。</summary>
    public static string ForNotificationAction(ReminderNotificationAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return $"{NotificationActionPrefix}{action.OccurrenceId:N}|{action.Action}|{action.OptionId}";
    }

    public static bool TryParseNotificationAction(string command, out ReminderNotificationAction action)
    {
        action = null!;
        if (!command.StartsWith(NotificationActionPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = command[NotificationActionPrefix.Length..].Split('|');
        if (parts.Length < 2 || !Guid.TryParseExact(parts[0], "N", out var occurrenceId))
        {
            return false;
        }

        var optionId = parts.Length >= 3 && parts[2].Length > 0 ? parts[2] : null;
        action = new ReminderNotificationAction(occurrenceId, parts[1], optionId);
        return true;
    }
}
