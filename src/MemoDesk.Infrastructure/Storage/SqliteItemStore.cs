using System.Globalization;
using MemoDesk.Core;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Drafting;
using MemoDesk.Core.Items;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Timeline;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Infrastructure.Storage;

/// <summary>
/// 事项写入与联动事务（设计 9 的事务边界）：
/// 确认草稿、完成/归档/删除都在单个 SQLite 事务内完成。
/// </summary>
public sealed class SqliteItemStore : IItemStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteItemStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public Item? GetById(Guid id)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectItems + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void Update(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();
        if (!UpdateItemCore(connection, transaction, item))
        {
            transaction.Rollback();
            throw new ItemVersionConflictException(item.Id);
        }

        transaction.Commit();
    }

    public MemoEditSaveResult UpdateWithReminder(
        Item item,
        ReminderEditIntent reminder,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(reminder);

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();
        try
        {
            if (!UpdateItemCore(connection, transaction, item))
            {
                transaction.Rollback();
                return new MemoEditSaveResult(MemoEditOutcome.VersionConflict, "备忘已被其他窗口或 AI 修改，请重新载入后再保存。");
            }

            if (reminder.Kind != ReminderEditKind.Unchanged)
            {
                var active = GetActivePlan(connection, transaction, item.Id);
                if (reminder.ExpectedReminderId is null)
                {
                    if (active is not null)
                    {
                        transaction.Rollback();
                        return new MemoEditSaveResult(MemoEditOutcome.ReminderVersionConflict, "提醒已被其他操作修改，请重新载入后再保存。");
                    }
                }
                else if (active is null
                    || active.Value.Id != reminder.ExpectedReminderId
                    || active.Value.PlanVersion != reminder.ExpectedPlanVersion)
                {
                    transaction.Rollback();
                    return new MemoEditSaveResult(MemoEditOutcome.ReminderVersionConflict, "提醒已被其他操作（例如稍后提醒）修改，请重新载入后再保存。");
                }

                if (reminder.Kind == ReminderEditKind.Set && reminder.TargetAtUtc is null)
                {
                    transaction.Rollback();
                    return new MemoEditSaveResult(MemoEditOutcome.ReminderInvalid, "提醒时间无效。");
                }

                if (reminder.Kind == ReminderEditKind.Set)
                {
                    if (active is null || active.Value.ReminderAtUtc != reminder.TargetAtUtc)
                    {
                        SqliteReminderRepository.SetReminderCore(
                            connection, transaction, item.Id, reminder.TargetAtUtc, now);
                    }
                }
                else if (active is not null)
                {
                    SqliteReminderRepository.SetReminderCore(
                        connection, transaction, item.Id, null, now);
                }
            }

            transaction.Commit();
            return MemoEditSaveResult.Success();
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            return new MemoEditSaveResult(MemoEditOutcome.Failure, $"保存失败：{ex.Message}");
        }
    }

    private static bool UpdateItemCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Item item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE items SET
                content = $content,
                tags_json = $tags,
                priority = $priority,
                item_date = $itemDate,
                due_at_utc = $dueAt,
                memo_date = $memoDate,
                work_date = $workDate,
                work_start_time = $workStart,
                work_end_time = $workEnd,
                work_end_date = $workEndDate,
                work_precision = $workPrecision,
                work_time_text = $workTimeText,
                updated_at_utc = $updatedAt,
                human_revised = 1,
                row_version = row_version + 1
            WHERE id = $id
              AND status <> 'Deleted'
              AND row_version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$content", item.Content);
        command.Parameters.AddWithValue("$tags", JsonDefaults.Serialize(item.Tags));
        command.Parameters.AddWithValue("$priority", (object?)item.Priority?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$itemDate", SqliteMapping.ToDb(item.ItemDate));
        command.Parameters.AddWithValue("$dueAt", SqliteMapping.ToDb(item.DueAtUtc));
        command.Parameters.AddWithValue("$memoDate", SqliteMapping.ToDb(item.MemoDate));
        command.Parameters.AddWithValue("$workDate", item.WorkTime is null ? DBNull.Value : SqliteMapping.ToDb(item.WorkTime.Date));
        command.Parameters.AddWithValue("$workStart", SqliteMapping.ToDb(item.WorkTime?.Start));
        command.Parameters.AddWithValue("$workEnd", SqliteMapping.ToDb(item.WorkTime?.End));
        command.Parameters.AddWithValue("$workEndDate", SqliteMapping.ToDb(item.WorkTime?.EndDate));
        command.Parameters.AddWithValue("$workPrecision", (object?)item.WorkTime?.Precision.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$workTimeText", (object?)item.WorkTime?.OriginalText ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", SqliteMapping.ToUtcText(item.UpdatedAtUtc));
        command.Parameters.AddWithValue("$id", item.Id.ToString("N"));
        command.Parameters.AddWithValue("$expectedVersion", item.RowVersion);
        return command.ExecuteNonQuery() == 1;
    }

    private static (Guid Id, int PlanVersion, DateTimeOffset ReminderAtUtc)? GetActivePlan(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid itemId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, plan_version, reminder_at_utc
            FROM reminders
            WHERE item_id = $itemId AND enabled = 1
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (
            Guid.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            SqliteMapping.ParseUtc(reader.GetString(2)));
    }

    public bool TrySetStatus(Guid itemId, ItemStatus status, DateTimeOffset now, Guid? reminderOccurrenceId = null)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        string type;
        string content;
        ItemStatus current;

        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT type, content, status FROM items WHERE id = $id;";
            read.Parameters.AddWithValue("$id", itemId.ToString("N"));
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            type = reader.GetString(0);
            content = reader.GetString(1);
            current = Enum.Parse<ItemStatus>(reader.GetString(2), ignoreCase: false);
        }

        if (!IsTransitionAllowed(current, status))
        {
            return false;
        }

        // 通知按钮路径：旧实例（版本不符、计划停用、事项已变化）不得产生业务效果。
        if (reminderOccurrenceId is { } occurrenceId)
        {
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = """
                SELECT o.status, o.plan_version, r.plan_version, r.enabled
                FROM reminder_occurrences o
                JOIN reminders r ON r.id = o.reminder_id
                WHERE o.id = $id AND o.item_id = $itemId;
                """;
            check.Parameters.AddWithValue("$id", occurrenceId.ToString("N"));
            check.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
            using var reader = check.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            var occurrenceStatus = reader.GetString(0);
            var occurrencePlanVersion = reader.GetInt32(1);
            var currentPlanVersion = reader.GetInt32(2);
            var planEnabled = reader.GetInt32(3) == 1;

            if (!planEnabled
                || occurrencePlanVersion != currentPlanVersion
                || occurrenceStatus is not ("Pending" or "Requested" or "Uncertain"))
            {
                return false;
            }
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE items SET
                    status = $status,
                    completed_at_utc = CASE WHEN $status = 'Completed' THEN $now
                                            WHEN $status = 'Open' THEN NULL
                                            ELSE completed_at_utc END,
                    archived_at_utc = CASE WHEN $status = 'Archived' THEN $now
                                           WHEN $status = 'Open' THEN NULL
                                           ELSE archived_at_utc END,
                    deleted_at_utc = CASE WHEN $status = 'Deleted' THEN $now ELSE deleted_at_utc END,
                    updated_at_utc = $now,
                    human_revised = 1,
                    row_version = row_version + 1
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$status", status.ToString());
            update.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            update.Parameters.AddWithValue("$id", itemId.ToString("N"));
            update.ExecuteNonQuery();
        }

        if (status == ItemStatus.Completed && type != ItemType.WorkLog.ToString())
        {
            using var timeline = connection.CreateCommand();
            timeline.Transaction = transaction;
            timeline.CommandText = """
                INSERT INTO timeline_events (id, item_id, event_type, occurred_at_utc, recorded_at_utc, content_snapshot)
                VALUES ($id, $itemId, $eventType, $occurredAt, $recordedAt, $snapshot);
                """;
            timeline.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            timeline.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
            timeline.Parameters.AddWithValue("$eventType", TimelineEventType.ItemCompleted.ToString());
            timeline.Parameters.AddWithValue("$occurredAt", SqliteMapping.ToUtcText(now));
            timeline.Parameters.AddWithValue("$recordedAt", SqliteMapping.ToUtcText(now));
            timeline.Parameters.AddWithValue("$snapshot", content);
            timeline.ExecuteNonQuery();
        }

        if (status is ItemStatus.Completed or ItemStatus.Archived or ItemStatus.Deleted)
        {
            var cancelReason = status switch
            {
                ItemStatus.Completed => "item-completed",
                ItemStatus.Archived => "item-archived",
                _ => "item-deleted",
            };

            // 已登记到系统的旧通知必须独立进入撤销任务，直到撤销成功。
            using (var enqueueCancellation = connection.CreateCommand())
            {
                enqueueCancellation.Transaction = transaction;
                enqueueCancellation.CommandText = """
                    INSERT INTO notification_cancellations (
                        id, reminder_id, plan_version, notification_tag, reason, state,
                        attempts, last_error, created_at_utc, updated_at_utc)
                    SELECT lower(hex(randomblob(16))), r.id, r.plan_version, r.scheduled_tag, $reason,
                           'pending', 0, NULL, $now, $now
                    FROM reminders r
                    WHERE r.item_id = $itemId
                      AND r.enabled = 1
                      AND r.scheduled_tag IS NOT NULL
                      AND r.schedule_state IN ('scheduled', 'registering')
                      AND NOT EXISTS (
                          SELECT 1 FROM notification_cancellations c
                          WHERE c.notification_tag = r.scheduled_tag AND c.state <> 'cancelled'
                      );
                    """;
                enqueueCancellation.Parameters.AddWithValue("$reason", cancelReason);
                enqueueCancellation.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
                enqueueCancellation.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
                enqueueCancellation.ExecuteNonQuery();
            }

            using var disableReminder = connection.CreateCommand();
            disableReminder.Transaction = transaction;
            disableReminder.CommandText = """
                UPDATE reminders
                SET enabled = 0,
                    plan_version = plan_version + 1,
                    delivery_owner = NULL,
                    scheduled_tag = NULL,
                    scheduled_at_utc = NULL,
                    schedule_state = NULL,
                    schedule_plan_version = NULL,
                    schedule_error = NULL,
                    schedule_updated_at = $now,
                    updated_at_utc = $now
                WHERE item_id = $itemId AND enabled = 1;
                """;
            disableReminder.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            disableReminder.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
            disableReminder.ExecuteNonQuery();

            using var cancelOccurrences = connection.CreateCommand();
            cancelOccurrences.Transaction = transaction;
            cancelOccurrences.CommandText = """
                UPDATE reminder_occurrences
                SET status = 'Cancelled',
                    cancel_reason = $reason,
                    delivery_owner = NULL,
                    delivery_lease_until_utc = NULL,
                    updated_at_utc = $now
                WHERE item_id = $itemId
                  AND status IN ('Pending', 'Requested', 'Uncertain')
                  AND ($excludeId IS NULL OR id <> $excludeId);
                """;
            cancelOccurrences.Parameters.AddWithValue("$reason", status switch
            {
                ItemStatus.Completed => "item-completed",
                ItemStatus.Archived => "item-archived",
                _ => "item-deleted",
            });
            cancelOccurrences.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            cancelOccurrences.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
            cancelOccurrences.Parameters.AddWithValue(
                "$excludeId",
                reminderOccurrenceId is { } excluded ? excluded.ToString("N") : DBNull.Value);
            cancelOccurrences.ExecuteNonQuery();
        }

        if (reminderOccurrenceId is { } completedOccurrenceId)
        {
            using var occurrence = connection.CreateCommand();
            occurrence.Transaction = transaction;
            occurrence.CommandText = """
                UPDATE reminder_occurrences
                SET status = 'Completed', action = 'completed', action_at_utc = $now,
                    delivery_owner = NULL, delivery_lease_until_utc = NULL, updated_at_utc = $now
                WHERE id = $id AND status IN ('Pending', 'Requested', 'Uncertain');
                """;
            occurrence.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            occurrence.Parameters.AddWithValue("$id", completedOccurrenceId.ToString("N"));
            occurrence.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    public DraftConfirmationOutcome ConfirmDraft(
        Guid draftId,
        int expectedDraftVersion,
        IReadOnlyList<Item> items,
        IReadOnlyList<ReminderPlan> reminders,
        IReadOnlyList<TimelineEvent> events,
        DateTimeOffset now,
        int expectedEditVersion = 0)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        string statusText;
        int version;
        int editVersion;

        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT status, version, edit_version FROM parse_drafts WHERE id = $id;";
            read.Parameters.AddWithValue("$id", draftId.ToString("N"));
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                return DraftConfirmationOutcome.DraftNotFound;
            }

            statusText = reader.GetString(0);
            version = reader.GetInt32(1);
            editVersion = reader.GetInt32(2);
        }

        var currentStatus = Enum.Parse<DraftStatus>(statusText, ignoreCase: false);
        if (currentStatus == DraftStatus.Confirmed)
        {
            return DraftConfirmationOutcome.AlreadyConfirmed;
        }

        if (currentStatus != DraftStatus.AwaitingConfirmation)
        {
            return DraftConfirmationOutcome.InvalidState;
        }

        if (version != expectedDraftVersion || editVersion != expectedEditVersion)
        {
            return DraftConfirmationOutcome.VersionConflict;
        }

        foreach (var item in items)
        {
            InsertItem(connection, transaction, item);
        }

        foreach (var reminder in reminders)
        {
            InsertReminder(connection, transaction, reminder);
        }

        foreach (var timelineEvent in events)
        {
            InsertTimeline(connection, transaction, timelineEvent);
        }

        using (var confirm = connection.CreateCommand())
        {
            confirm.Transaction = transaction;
            confirm.CommandText = """
                UPDATE parse_drafts SET status = 'Confirmed', last_error = NULL, updated_at_utc = $now
                WHERE id = $id AND version = $version AND edit_version = $editVersion
                  AND status = 'AwaitingConfirmation';
                """;
            confirm.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            confirm.Parameters.AddWithValue("$id", draftId.ToString("N"));
            confirm.Parameters.AddWithValue("$version", expectedDraftVersion);
            confirm.Parameters.AddWithValue("$editVersion", expectedEditVersion);
            if (confirm.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return DraftConfirmationOutcome.VersionConflict;
            }
        }

        transaction.Commit();
        return DraftConfirmationOutcome.Confirmed;
    }

    private static bool IsTransitionAllowed(ItemStatus current, ItemStatus target) => target switch
    {
        ItemStatus.Completed => current == ItemStatus.Open,
        ItemStatus.Open => current is ItemStatus.Completed or ItemStatus.Archived,
        ItemStatus.Archived => current == ItemStatus.Open,
        ItemStatus.Deleted => current != ItemStatus.Deleted,
        _ => false,
    };

    internal static void InsertItem(SqliteConnection connection, SqliteTransaction transaction, Item item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO items (
                id, capture_id, candidate_source_id, type, content, tags_json, priority, status,
                item_date, due_at_utc, memo_date,
                work_date, work_start_time, work_end_time, work_end_date, work_precision, work_time_text,
                created_at_utc, updated_at_utc, completed_at_utc, archived_at_utc, deleted_at_utc, row_version, human_revised)
            VALUES (
                $id, $captureId, $candidateId, $type, $content, $tags, $priority, $status,
                $itemDate, $dueAt, $memoDate,
                $workDate, $workStart, $workEnd, $workEndDate, $workPrecision, $workTimeText,
                $createdAt, $updatedAt, $completedAt, $archivedAt, $deletedAt, $rowVersion, $humanRevised);
            """;
        command.Parameters.AddWithValue("$id", item.Id.ToString("N"));
        command.Parameters.AddWithValue("$captureId", (object?)item.CaptureId?.ToString("N") ?? DBNull.Value);
        command.Parameters.AddWithValue("$candidateId", (object?)item.CandidateSourceId?.ToString("N") ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", item.Type.ToString());
        command.Parameters.AddWithValue("$content", item.Content);
        command.Parameters.AddWithValue("$tags", JsonDefaults.Serialize(item.Tags));
        command.Parameters.AddWithValue("$priority", (object?)item.Priority?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", item.Status.ToString());
        command.Parameters.AddWithValue("$itemDate", SqliteMapping.ToDb(item.ItemDate));
        command.Parameters.AddWithValue("$dueAt", SqliteMapping.ToDb(item.DueAtUtc));
        command.Parameters.AddWithValue("$memoDate", SqliteMapping.ToDb(item.MemoDate));
        command.Parameters.AddWithValue("$workDate", item.WorkTime is null ? DBNull.Value : SqliteMapping.ToDb(item.WorkTime.Date));
        command.Parameters.AddWithValue("$workStart", SqliteMapping.ToDb(item.WorkTime?.Start));
        command.Parameters.AddWithValue("$workEnd", SqliteMapping.ToDb(item.WorkTime?.End));
        command.Parameters.AddWithValue("$workEndDate", SqliteMapping.ToDb(item.WorkTime?.EndDate));
        command.Parameters.AddWithValue("$workPrecision", (object?)item.WorkTime?.Precision.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$workTimeText", (object?)item.WorkTime?.OriginalText ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", SqliteMapping.ToUtcText(item.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedAt", SqliteMapping.ToUtcText(item.UpdatedAtUtc));
        command.Parameters.AddWithValue("$completedAt", SqliteMapping.ToDb(item.CompletedAtUtc));
        command.Parameters.AddWithValue("$archivedAt", SqliteMapping.ToDb(item.ArchivedAtUtc));
        command.Parameters.AddWithValue("$deletedAt", SqliteMapping.ToDb(item.DeletedAtUtc));
        command.Parameters.AddWithValue("$rowVersion", item.RowVersion);
        command.Parameters.AddWithValue("$humanRevised", item.HumanRevised ? 1 : 0);
        command.ExecuteNonQuery();
    }

    internal static void InsertReminder(SqliteConnection connection, SqliteTransaction transaction, ReminderPlan reminder)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reminders (id, item_id, reminder_at_utc, enabled, plan_version, created_at_utc, updated_at_utc,
                source, relative_seconds, captured_at_utc, source_time_text)
            VALUES ($id, $itemId, $reminderAt, $enabled, $planVersion, $createdAt, $updatedAt,
                $source, $seconds, $captured, $sourceText);
            """;
        command.Parameters.AddWithValue("$id", reminder.Id.ToString("N"));
        command.Parameters.AddWithValue("$itemId", reminder.ItemId.ToString("N"));
        command.Parameters.AddWithValue("$reminderAt", SqliteMapping.ToUtcText(reminder.ReminderAtUtc));
        command.Parameters.AddWithValue("$enabled", reminder.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$planVersion", reminder.PlanVersion);
        command.Parameters.AddWithValue("$createdAt", SqliteMapping.ToUtcText(reminder.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedAt", SqliteMapping.ToUtcText(reminder.UpdatedAtUtc));
        command.Parameters.AddWithValue("$source", reminder.Source);
        command.Parameters.AddWithValue("$seconds", (object?)reminder.RelativeSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$captured", reminder.CapturedAtUtc is { } captured ? SqliteMapping.ToUtcText(captured) : DBNull.Value);
        command.Parameters.AddWithValue("$sourceText", (object?)reminder.SourceTimeText ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    internal static void InsertTimeline(SqliteConnection connection, SqliteTransaction transaction, TimelineEvent timelineEvent)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO timeline_events (id, item_id, event_type, occurred_at_utc, recorded_at_utc, content_snapshot)
            VALUES ($id, $itemId, $eventType, $occurredAt, $recordedAt, $snapshot);
            """;
        command.Parameters.AddWithValue("$id", timelineEvent.Id.ToString("N"));
        command.Parameters.AddWithValue("$itemId", (object?)timelineEvent.ItemId?.ToString("N") ?? DBNull.Value);
        command.Parameters.AddWithValue("$eventType", timelineEvent.EventType.ToString());
        command.Parameters.AddWithValue("$occurredAt", SqliteMapping.ToUtcText(timelineEvent.OccurredAtUtc));
        command.Parameters.AddWithValue("$recordedAt", SqliteMapping.ToUtcText(timelineEvent.RecordedAtUtc));
        command.Parameters.AddWithValue("$snapshot", timelineEvent.ContentSnapshot);
        command.ExecuteNonQuery();
    }

    internal const string SelectItems = """
        SELECT id, capture_id, candidate_source_id, type, content, tags_json, priority, status,
               item_date, due_at_utc, memo_date,
               work_date, work_start_time, work_end_time, work_end_date, work_precision, work_time_text,
               created_at_utc, updated_at_utc, completed_at_utc, archived_at_utc, deleted_at_utc, row_version, human_revised
        FROM items
        """;

    internal static Item Map(SqliteDataReader reader)
    {
        IReadOnlyList<string> tags;
        try
        {
            tags = JsonDefaults.Deserialize<List<string>>(reader.GetString(5)) ?? new List<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            tags = Array.Empty<string>();
        }

        WorkTimeInfo? workTime = null;
        if (!reader.IsDBNull(11))
        {
            workTime = new WorkTimeInfo(
                Date: SqliteMapping.ParseDate(reader.GetString(11)),
                Start: SqliteMapping.ParseTimeOrNull(reader.IsDBNull(12) ? null : reader.GetString(12)),
                End: SqliteMapping.ParseTimeOrNull(reader.IsDBNull(13) ? null : reader.GetString(13)),
                OriginalText: reader.IsDBNull(16) ? null : reader.GetString(16),
                Precision: reader.IsDBNull(15)
                    ? WorkTimePrecision.Date
                    : Enum.Parse<WorkTimePrecision>(reader.GetString(15), ignoreCase: false),
                EndDate: SqliteMapping.ParseDateOrNull(reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return new Item
        {
            Id = Guid.Parse(reader.GetString(0)),
            CaptureId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
            CandidateSourceId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            Type = Enum.Parse<ItemType>(reader.GetString(3), ignoreCase: false),
            Content = reader.GetString(4),
            Tags = tags,
            Priority = reader.IsDBNull(6) ? null : Enum.Parse<ItemPriority>(reader.GetString(6), ignoreCase: false),
            Status = Enum.Parse<ItemStatus>(reader.GetString(7), ignoreCase: false),
            ItemDate = SqliteMapping.ParseDateOrNull(reader.IsDBNull(8) ? null : reader.GetString(8)),
            DueAtUtc = SqliteMapping.ParseUtcOrNull(reader.IsDBNull(9) ? null : reader.GetString(9)),
            MemoDate = SqliteMapping.ParseDateOrNull(reader.IsDBNull(10) ? null : reader.GetString(10)),
            WorkTime = workTime,
            CreatedAtUtc = SqliteMapping.ParseUtc(reader.GetString(17)),
            UpdatedAtUtc = SqliteMapping.ParseUtc(reader.GetString(18)),
            CompletedAtUtc = SqliteMapping.ParseUtcOrNull(reader.IsDBNull(19) ? null : reader.GetString(19)),
            ArchivedAtUtc = SqliteMapping.ParseUtcOrNull(reader.IsDBNull(20) ? null : reader.GetString(20)),
            DeletedAtUtc = SqliteMapping.ParseUtcOrNull(reader.IsDBNull(21) ? null : reader.GetString(21)),
            RowVersion = reader.GetInt32(22),
            HumanRevised = reader.GetInt32(23) == 1,
        };
    }
}
