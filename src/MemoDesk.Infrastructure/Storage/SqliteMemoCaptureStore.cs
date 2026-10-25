using MemoDesk.Core.Capturing;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Timeline;
using MemoDesk.Core.Parsing;
using MemoDesk.Core.Reminders;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Infrastructure.Storage;

/// <summary>即时捕获的 SQLite 实现：原文、备忘、录入事件和迁移上下文同一事务提交。</summary>
public sealed class SqliteMemoCaptureStore : IMemoCaptureStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteMemoCaptureStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public MemoCaptureResult Save(Capture capture, Item memo, TimelineEvent capturedEvent,
        LocalReminderResolution? reminder = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(memo);
        ArgumentNullException.ThrowIfNull(capturedEvent);

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        if (CaptureExists(connection, transaction, capture.Id))
        {
            var existing = FindMemo(connection, transaction, capture.Id);
            if (existing is not null)
            {
                transaction.Commit();
                return new MemoCaptureResult(capture, existing, AlreadyExisted: true);
            }
        }
        else
        {
            InsertCapture(connection, transaction, capture);
        }

        SqliteItemStore.InsertItem(connection, transaction, memo);

        if (reminder is not null)
        {
            SqliteItemStore.InsertReminder(connection, transaction, new ReminderPlan(
                Guid.NewGuid(), memo.Id, reminder.TargetAtUtc, true, 1,
                capture.CapturedAtUtc, capture.CapturedAtUtc, Source: "LocalExplicit",
                RelativeSeconds: reminder.RelativeSeconds, CapturedAtUtc: capture.CapturedAtUtc,
                SourceTimeText: reminder.SourceText));
        }

        if (!CaptureEventExists(connection, transaction, memo.Id))
        {
            SqliteItemStore.InsertTimeline(connection, transaction, capturedEvent);
        }

        using (var context = connection.CreateCommand())
        {
            context.Transaction = transaction;
            context.CommandText = """
                INSERT INTO migration_context (capture_id, state, error, applied_at_utc)
                VALUES ($captureId, 'current', NULL, $now)
                ON CONFLICT(capture_id) DO UPDATE SET
                    state = 'current',
                    error = NULL,
                    applied_at_utc = excluded.applied_at_utc;
                """;
            context.Parameters.AddWithValue("$captureId", capture.Id.ToString("N"));
            context.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(capture.RecordedAtUtc));
            context.ExecuteNonQuery();
        }

        transaction.Commit();
        return new MemoCaptureResult(capture, memo, AlreadyExisted: false, reminder);
    }

    private static bool CaptureExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM captures WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", captureId.ToString("N"));
        return command.ExecuteScalar() is not null;
    }

    private static Item? FindMemo(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SqliteItemStore.SelectItems + " WHERE capture_id = $captureId ORDER BY created_at_utc ASC LIMIT 1;";
        command.Parameters.AddWithValue("$captureId", captureId.ToString("N"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? SqliteItemStore.Map(reader) : null;
    }

    private static bool CaptureEventExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid itemId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM timeline_events
            WHERE item_id = $itemId AND event_type = 'MemoCaptured'
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
        return command.ExecuteScalar() is not null;
    }

    private static void InsertCapture(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Capture capture)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO captures (
                id, raw_text, captured_at_utc, captured_time_zone_id,
                parse_status, last_error, recorded_at_utc, parse_operation_version)
            VALUES (
                $id, $rawText, $capturedAtUtc, $capturedTimeZoneId,
                $parseStatus, $lastError, $recordedAtUtc, $operationVersion);
            """;
        command.Parameters.AddWithValue("$id", capture.Id.ToString("N"));
        command.Parameters.AddWithValue("$rawText", capture.RawText);
        command.Parameters.AddWithValue("$capturedAtUtc", SqliteMapping.ToUtcText(capture.CapturedAtUtc));
        command.Parameters.AddWithValue("$capturedTimeZoneId", capture.CapturedTimeZoneId);
        command.Parameters.AddWithValue("$parseStatus", capture.ParseStatus.ToString());
        command.Parameters.AddWithValue("$lastError", (object?)capture.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$recordedAtUtc", SqliteMapping.ToUtcText(capture.RecordedAtUtc));
        command.Parameters.AddWithValue("$operationVersion", capture.ParseOperationVersion);
        command.ExecuteNonQuery();
    }
}
