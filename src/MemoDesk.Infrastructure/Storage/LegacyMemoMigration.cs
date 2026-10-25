using System.Globalization;
using MemoDesk.Core;
using MemoDesk.Core.Capturing;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Drafting;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Time;
using MemoDesk.Core.Timeline;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MemoDesk.Infrastructure.Storage;

/// <summary>
/// v10 之后的旧数据迁移：按原文分组把旧草稿/原文转换成正式备忘。
/// 与 SQL 迁移分开，因为要读取 JSON、判断人工编辑结果并按组重试；迁移上下文保证幂等。
/// </summary>
public sealed class LegacyMemoMigration
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger<LegacyMemoMigration> _logger;

    public LegacyMemoMigration(
        SqliteConnectionFactory connectionFactory,
        TimeProvider clock,
        ILogger<LegacyMemoMigration> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Run()
    {
        if (!HasMigrationContextTable())
        {
            return;
        }

        var pending = LoadPendingCaptures();
        if (pending.Count == 0)
        {
            return;
        }

        BackupBeforeConversion();

        foreach (var capture in pending)
        {
            try
            {
                MigrateOne(capture);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "旧数据迁移失败，原文：{CaptureId}", capture.Id);
                SaveContext(capture.Id, "error", ex.Message);
            }
        }
    }

    private bool HasMigrationContextTable()
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM sqlite_master
                WHERE type = 'table' AND name = 'migration_context');
            """;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private List<CaptureRow> LoadPendingCaptures()
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.raw_text, c.captured_at_utc, c.captured_time_zone_id, c.parse_status
            FROM captures c
            LEFT JOIN migration_context m ON m.capture_id = c.id
            WHERE m.capture_id IS NULL OR m.state = 'error'
            ORDER BY c.captured_at_utc ASC;
            """;

        var result = new List<CaptureRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CaptureRow(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                SqliteMapping.ParseUtc(reader.GetString(2)),
                reader.GetString(3),
                Enum.Parse<CaptureParseStatus>(reader.GetString(4), ignoreCase: false)));
        }

        return result;
    }

    private void MigrateOne(CaptureRow capture)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        var state = ConvertCapture(connection, transaction, capture);

        using (var context = connection.CreateCommand())
        {
            context.Transaction = transaction;
            context.CommandText = """
                INSERT INTO migration_context (capture_id, state, error, applied_at_utc)
                VALUES ($captureId, $state, NULL, $now)
                ON CONFLICT(capture_id) DO UPDATE SET
                    state = excluded.state,
                    error = NULL,
                    applied_at_utc = excluded.applied_at_utc;
                """;
            context.Parameters.AddWithValue("$captureId", capture.Id.ToString("N"));
            context.Parameters.AddWithValue("$state", state);
            context.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(_clock.GetUtcNow()));
            context.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private string ConvertCapture(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureRow capture)
    {
        var existingItems = LoadItems(connection, transaction, capture.Id);
        if (existingItems.Count > 0)
        {
            var existingCaptureDate = BusinessTime.DateOf(
                capture.CapturedAtUtc,
                BusinessTime.Resolve(capture.TimeZoneId));
            foreach (var item in existingItems)
            {
                if (item.Type is ItemType.Todo or ItemType.Memo && item.MemoDate is null)
                {
                    SetMemoDate(connection, transaction, item.Id, item.ItemDate ?? existingCaptureDate);
                }

                if (item.Type != ItemType.WorkLog)
                {
                    EnsureCaptureEvent(connection, transaction, capture, item);
                }
            }

            SupersedeResidualDrafts(connection, transaction, capture.Id);
            UpdateCapture(connection, transaction, capture.Id, CaptureParseStatus.Parsed, null);
            return "kept-existing";
        }

        var draft = LoadLatestDraft(connection, transaction, capture.Id);
        if (draft is { Status: DraftStatus.Discarded } || capture.ParseStatus == CaptureParseStatus.Discarded)
        {
            return "skipped-discarded";
        }

        if (draft is { Status: DraftStatus.Confirmed })
        {
            UpdateCapture(connection, transaction, capture.Id, CaptureParseStatus.Parsed, null);
            return "skipped-confirmed";
        }

        IReadOnlyList<DraftCandidate> candidates = draft is null
            ? Array.Empty<DraftCandidate>()
            : BuildCandidates(draft, capture);

        if (candidates.Count == 0)
        {
            CreateRawMemo(connection, transaction, capture);
            return "created-raw";
        }

        var timeZone = BusinessTime.Resolve(capture.TimeZoneId);
        var captureDate = BusinessTime.DateOf(capture.CapturedAtUtc, timeZone);
        var now = _clock.GetUtcNow();
        Guid? firstMemoId = null;

        foreach (var candidate in candidates)
        {
            var content = candidate.Content.Trim();
            if (content.Length == 0)
            {
                continue;
            }

            var item = new Item
            {
                Id = Guid.NewGuid(),
                CaptureId = capture.Id,
                CandidateSourceId = candidate.SourceId,
                Type = candidate.Type,
                Content = content,
                Tags = candidate.Tags.ToArray(),
                Priority = candidate.Priority,
                Status = ItemStatus.Open,
                ItemDate = candidate.Type == ItemType.Todo ? candidate.BusinessDate : null,
                DueAtUtc = candidate.DueAtUtc,
                MemoDate = candidate.Type is ItemType.Todo or ItemType.Memo
                    ? candidate.BusinessDate ?? captureDate
                    : null,
                WorkTime = candidate.WorkTime,
                CreatedAtUtc = capture.CapturedAtUtc,
                UpdatedAtUtc = now,
                HumanRevised = draft?.EditStateJson is not null,
            };

            SqliteItemStore.InsertItem(connection, transaction, item);
            firstMemoId ??= item.Id;

            SqliteItemStore.InsertTimeline(
                connection,
                transaction,
                new TimelineEvent(
                    Guid.NewGuid(),
                    item.Id,
                    TimelineEventType.MemoCaptured,
                    capture.CapturedAtUtc,
                    now,
                    item.Content));

            if (candidate.RemindAtUtc is { } remindAt
                && candidate.Type is ItemType.Todo or ItemType.Memo
                && remindAt > now
                && candidate.ValidationErrors.Count == 0)
            {
                SqliteItemStore.InsertReminder(
                    connection,
                    transaction,
                    new ReminderPlan(
                        Guid.NewGuid(),
                        item.Id,
                        remindAt,
                        Enabled: true,
                        PlanVersion: 1,
                        CreatedAtUtc: now,
                        UpdatedAtUtc: now));
            }
        }

        if (firstMemoId is null)
        {
            CreateRawMemo(connection, transaction, capture);
            return "created-raw";
        }

        using (var markDraft = connection.CreateCommand())
        {
            markDraft.Transaction = transaction;
            markDraft.CommandText = """
                UPDATE parse_drafts
                SET status = 'Applied',
                    applied_at_utc = $now,
                    applied_memo_id = $memoId,
                    updated_at_utc = $now
                WHERE capture_id = $captureId
                  AND status NOT IN ('Applied', 'Discarded');
                """;
            markDraft.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            markDraft.Parameters.AddWithValue("$memoId", firstMemoId.Value.ToString("N"));
            markDraft.Parameters.AddWithValue("$captureId", capture.Id.ToString("N"));
            markDraft.ExecuteNonQuery();
        }

        UpdateCapture(connection, transaction, capture.Id, CaptureParseStatus.Parsed, null);
        return "converted-draft";
    }

    private void CreateRawMemo(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureRow capture)
    {
        var timeZone = BusinessTime.Resolve(capture.TimeZoneId);
        var captureDate = BusinessTime.DateOf(capture.CapturedAtUtc, timeZone);
        var now = _clock.GetUtcNow();
        var item = new Item
        {
            Id = Guid.NewGuid(),
            CaptureId = capture.Id,
            CandidateSourceId = capture.Id,
            Type = ItemType.Memo,
            Content = capture.RawText.Trim(),
            Status = ItemStatus.Open,
            MemoDate = captureDate,
            CreatedAtUtc = capture.CapturedAtUtc,
            UpdatedAtUtc = now,
        };

        SqliteItemStore.InsertItem(connection, transaction, item);
        SqliteItemStore.InsertTimeline(
            connection,
            transaction,
            new TimelineEvent(
                Guid.NewGuid(),
                item.Id,
                TimelineEventType.MemoCaptured,
                capture.CapturedAtUtc,
                now,
                item.Content));

        // Failed 保留“按原文保存”状态；其余可重新交给新解析链路补充。
        if (capture.ParseStatus != CaptureParseStatus.Failed)
        {
            UpdateCapture(connection, transaction, capture.Id, CaptureParseStatus.Pending, null);
        }
    }

    private void EnsureCaptureEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureRow capture,
        Item item)
    {
        using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = """
            SELECT 1 FROM timeline_events
            WHERE item_id = $itemId AND event_type = 'MemoCaptured'
            LIMIT 1;
            """;
        exists.Parameters.AddWithValue("$itemId", item.Id.ToString("N"));
        if (exists.ExecuteScalar() is not null)
        {
            return;
        }

        SqliteItemStore.InsertTimeline(
            connection,
            transaction,
            new TimelineEvent(
                Guid.NewGuid(),
                item.Id,
                TimelineEventType.MemoCaptured,
                capture.CapturedAtUtc,
                _clock.GetUtcNow(),
                item.Content));
    }

    private static void SupersedeResidualDrafts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE parse_drafts
            SET status = 'Superseded',
                last_error = COALESCE(last_error, 'existing-items'),
                updated_at_utc = $now
            WHERE capture_id = $captureId
              AND status IN ('AwaitingConfirmation', 'Failed', 'Parsing');
            """;
        command.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$captureId", captureId.ToString("N"));
        command.ExecuteNonQuery();
    }

    private static void UpdateCapture(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId,
        CaptureParseStatus status,
        string? error)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE captures
            SET parse_status = $status, last_error = $error
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", captureId.ToString("N"));
        command.ExecuteNonQuery();
    }

    private static void SetMemoDate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid itemId,
        DateOnly date)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE items SET memo_date = $date WHERE id = $id AND memo_date IS NULL;";
        command.Parameters.AddWithValue("$date", SqliteMapping.ToDb(date));
        command.Parameters.AddWithValue("$id", itemId.ToString("N"));
        command.ExecuteNonQuery();
    }

    private static List<Item> LoadItems(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SqliteItemStore.SelectItems + " WHERE capture_id = $captureId;";
        command.Parameters.AddWithValue("$captureId", captureId.ToString("N"));

        var result = new List<Item>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(SqliteItemStore.Map(reader));
        }

        return result;
    }

    private static DraftRow? LoadLatestDraft(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, status, candidates_json, edit_state_json
            FROM parse_drafts
            WHERE capture_id = $captureId
            ORDER BY version DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$captureId", captureId.ToString("N"));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        IReadOnlyList<DraftCandidate> candidates = Array.Empty<DraftCandidate>();
        try
        {
            candidates = JsonDefaults.Deserialize<List<DraftCandidate>>(reader.GetString(2))
                ?? new List<DraftCandidate>();
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return new DraftRow(
            Guid.Parse(reader.GetString(0)),
            Enum.Parse<DraftStatus>(reader.GetString(1), ignoreCase: false),
            candidates,
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static List<DraftCandidate> BuildCandidates(DraftRow draft, CaptureRow capture)
    {
        var timeZone = BusinessTime.Resolve(capture.TimeZoneId);
        var captureDate = BusinessTime.DateOf(capture.CapturedAtUtc, timeZone);

        if (string.IsNullOrWhiteSpace(draft.EditStateJson))
        {
            return draft.Candidates.ToList();
        }

        DraftEditState? editState;
        try
        {
            editState = JsonDefaults.Deserialize<DraftEditState>(draft.EditStateJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return draft.Candidates.ToList();
        }

        if (editState?.Candidates is not { Count: > 0 })
        {
            return new List<DraftCandidate>();
        }

        var originals = draft.Candidates.ToDictionary(candidate => candidate.SourceId);
        var result = new List<DraftCandidate>();
        foreach (var edit in editState.Candidates)
        {
            var original = originals.TryGetValue(edit.SourceId, out var found)
                ? found
                : new DraftCandidate
                {
                    SourceId = edit.SourceId,
                    Type = edit.Type,
                    Content = edit.Content,
                };

            var candidate = DraftCandidateEditor.Apply(original, edit, timeZone, captureDate);
            candidate.ValidationErrors.Clear();
            result.Add(candidate);
        }

        return result;
    }

    private void SaveContext(Guid captureId, string state, string? error)
    {
        try
        {
            using var connection = _connectionFactory.Create();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO migration_context (capture_id, state, error, applied_at_utc)
                VALUES ($captureId, $state, $error, $now)
                ON CONFLICT(capture_id) DO UPDATE SET
                    state = excluded.state,
                    error = excluded.error,
                    applied_at_utc = excluded.applied_at_utc;
                """;
            command.Parameters.AddWithValue("$captureId", captureId.ToString("N"));
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(_clock.GetUtcNow()));
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "记录旧数据迁移错误失败：{CaptureId}", captureId);
        }
    }

    private void BackupBeforeConversion()
    {
        var backupPath = _connectionFactory.DatabasePath + ".pre-memo-v1.bak";
        if (File.Exists(backupPath))
        {
            return;
        }

        try
        {
            using var source = _connectionFactory.Create();
            using var destination = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = backupPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                }.ToString());
            destination.Open();
            source.BackupDatabase(destination);
            _logger.LogInformation("旧数据迁移备份已创建：{BackupPath}", backupPath);
        }
        catch (Exception ex)
        {
            // 备份失败不应让应用无法启动；迁移仍会在原库上按组提交。
            _logger.LogWarning(ex, "旧数据迁移备份失败，继续执行迁移。");
        }
    }

    private sealed record CaptureRow(
        Guid Id,
        string RawText,
        DateTimeOffset CapturedAtUtc,
        string TimeZoneId,
        CaptureParseStatus ParseStatus);

    private sealed record DraftRow(
        Guid Id,
        DraftStatus Status,
        IReadOnlyList<DraftCandidate> Candidates,
        string? EditStateJson);
}
