using MemoDesk.Core.Domain;
using MemoDesk.Core.Reminders;
using MemoDesk.Infrastructure.Storage;

namespace MemoDesk.Infrastructure.Queries;

public enum ReminderViewFilter
{
    Unreminded = 0,
    Requested = 1,
    Snoozed = 2,
    Problem = 3,
    All = 4,
}

public sealed record ReminderOverviewRow(
    Guid ReminderId,
    Guid ItemId,
    ItemType ItemType,
    string Content,
    DateTimeOffset PlanAtUtc,
    bool PlanEnabled,
    int PlanVersion,
    Guid? OccurrenceId,
    ReminderOccurrenceStatus? OccurrenceStatus,
    DateTimeOffset? ScheduledAtUtc,
    DateTimeOffset? HandedOffAtUtc,
    DateTimeOffset? ActionAtUtc,
    int RecoveryAttempts,
    string? LastError);

/// <summary>提醒状态查询（设计 5.5）：计划 + 最新实例 + 事项。</summary>
public sealed class ReminderQueryService
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public ReminderQueryService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public IReadOnlyList<ReminderOverviewRow> GetOverview()
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id, r.item_id, i.type, i.content, r.reminder_at_utc, r.enabled, r.plan_version,
                   o.id, o.status, o.scheduled_at_utc, o.handed_off_at_utc, o.action_at_utc,
                   COALESCE(o.recovery_attempts, 0), o.last_error
            FROM reminders r
            JOIN items i ON i.id = r.item_id
            LEFT JOIN reminder_occurrences o ON o.id = (
                SELECT id FROM reminder_occurrences
                WHERE reminder_id = r.id
                ORDER BY created_at_utc DESC
                LIMIT 1)
            WHERE i.status <> 'Deleted'
            ORDER BY r.updated_at_utc DESC;
            """;

        var result = new List<ReminderOverviewRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ReminderOverviewRow(
                ReminderId: Guid.Parse(reader.GetString(0)),
                ItemId: Guid.Parse(reader.GetString(1)),
                ItemType: Enum.Parse<ItemType>(reader.GetString(2), ignoreCase: false),
                Content: reader.GetString(3),
                PlanAtUtc: SqliteMapping.ParseUtc(reader.GetString(4)),
                PlanEnabled: reader.GetInt32(5) == 1,
                PlanVersion: reader.GetInt32(6),
                OccurrenceId: reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
                OccurrenceStatus: reader.IsDBNull(8)
                    ? null
                    : Enum.Parse<ReminderOccurrenceStatus>(reader.GetString(8), ignoreCase: false),
                ScheduledAtUtc: SqliteMapping.ParseUtcOrNull(reader.IsDBNull(9) ? null : reader.GetString(9)),
                HandedOffAtUtc: SqliteMapping.ParseUtcOrNull(reader.IsDBNull(10) ? null : reader.GetString(10)),
                ActionAtUtc: SqliteMapping.ParseUtcOrNull(reader.IsDBNull(11) ? null : reader.GetString(11)),
                RecoveryAttempts: reader.GetInt32(12),
                LastError: reader.IsDBNull(13) ? null : reader.GetString(13)));
        }

        return result;
    }

    public static bool MatchesFilter(ReminderOverviewRow row, ReminderViewFilter filter) => filter switch
    {
        ReminderViewFilter.Unreminded => row.PlanEnabled && row.OccurrenceStatus is null or ReminderOccurrenceStatus.Pending,
        ReminderViewFilter.Requested => row.OccurrenceStatus == ReminderOccurrenceStatus.Requested,
        ReminderViewFilter.Snoozed => row.OccurrenceStatus == ReminderOccurrenceStatus.Snoozed,
        ReminderViewFilter.Problem => row.OccurrenceStatus is ReminderOccurrenceStatus.Failed
            or ReminderOccurrenceStatus.Uncertain
            or ReminderOccurrenceStatus.Cancelled,
        _ => true,
    };
}
