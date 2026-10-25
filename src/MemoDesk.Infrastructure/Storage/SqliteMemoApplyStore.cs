using MemoDesk.Core;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Parsing;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Time;
using MemoDesk.Core.Timeline;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Infrastructure.Storage;

/// <summary>
/// 整理结果应用事务：校验原文操作版本和初始备忘并发状态，再更新初始备忘、
/// 插入拆分备忘、提醒和录入事件；人工修改/完成/归档/删除后晚到结果只能被跳过。
/// </summary>
public sealed class SqliteMemoApplyStore : IMemoApplyStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteMemoApplyStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public MemoApplyOutcome Apply(Guid parseResultId, DateTimeOffset now)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        if (!LoadResult(connection, transaction, parseResultId, out var result))
        {
            return MemoApplyOutcome.NotFound;
        }

        if (result.Status == MemoParseStatus.Applied || result.AppliedAtUtc is not null)
        {
            return MemoApplyOutcome.AlreadyApplied;
        }

        if (result.Status != MemoParseStatus.AwaitingApply)
        {
            return MemoApplyOutcome.NotFound;
        }

        if (!LoadCapture(connection, transaction, result.CaptureId, out var capture))
        {
            MarkSkipped(connection, transaction, parseResultId, "capture-missing", now);
            transaction.Commit();
            return MemoApplyOutcome.NotFound;
        }

        if (capture.OperationVersion != result.OperationVersion)
        {
            MarkSkipped(connection, transaction, parseResultId, "parse-operation-stale", now);
            transaction.Commit();
            return MemoApplyOutcome.VersionConflict;
        }

        var initial = FindInitialMemo(connection, transaction, result.CaptureId);
        if (initial is null)
        {
            MarkSkipped(connection, transaction, parseResultId, "memo-missing", now);
            transaction.Commit();
            return MemoApplyOutcome.MissingMemo;
        }

        if (initial.Status != ItemStatus.Open)
        {
            MarkSkipped(connection, transaction, parseResultId, "item-not-open", now);
            transaction.Commit();
            return MemoApplyOutcome.ItemNotOpen;
        }

        if (initial.HumanRevised)
        {
            MarkSkipped(connection, transaction, parseResultId, "human-revised", now);
            transaction.Commit();
            return MemoApplyOutcome.HumanRevised;
        }

        var candidates = result.Candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Content))
            .ToList();
        // 本地即时通道仅接受单一命令。AI 整理只更新同一备忘，不再次拆分或重建计划。
        using (var local = connection.CreateCommand())
        {
            local.Transaction = transaction;
            local.CommandText = "SELECT 1 FROM reminders WHERE item_id = $item AND source = 'LocalExplicit' LIMIT 1;";
            local.Parameters.AddWithValue("$item", initial.Id.ToString("N"));
            if (local.ExecuteScalar() is not null) candidates = candidates.Take(1).ToList();
        }
        if (candidates.Count == 0)
        {
            MarkSkipped(connection, transaction, parseResultId, "empty-result", now);
            transaction.Commit();
            return MemoApplyOutcome.EmptyResult;
        }

        var timeZone = BusinessTime.Resolve(capture.TimeZoneId);
        var captureDate = BusinessTime.DateOf(capture.CapturedAtUtc, timeZone);
        var migrationState = LoadMigrationState(connection, transaction, result.CaptureId);
        var decisions = MemoReminderRules.EvaluateAll(
            capture.RawText,
            candidates,
            capture.CapturedAtUtc,
            timeZone);

        if (!UpdateInitialMemo(connection, transaction, initial, candidates[0], now))
        {
            MarkSkipped(connection, transaction, parseResultId, "memo-version-conflict", now);
            transaction.Commit();
            return MemoApplyOutcome.HumanRevised;
        }

        EnsureCapturedEvent(connection, transaction, capture, initial, now);
        AddReminderIfNeeded(connection, transaction, initial.Id, decisions[0], migrationState, capture, now);

        for (var index = 1; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var memoId = StableId.ExtraMemo(result.CaptureId, result.OperationVersion, index);
            var item = new Item
            {
                Id = memoId,
                CaptureId = result.CaptureId,
                CandidateSourceId = candidate.SourceId,
                Type = ItemType.Memo,
                Content = candidate.Content.Trim(),
                Tags = NormalizeTags(candidate.Tags),
                Priority = candidate.Priority,
                Status = ItemStatus.Open,
                MemoDate = captureDate,
                WorkTime = candidate.WorkTime,
                CreatedAtUtc = capture.CapturedAtUtc,
                UpdatedAtUtc = now,
            };

            SqliteItemStore.InsertItem(connection, transaction, item);
            EnsureCapturedEvent(connection, transaction, capture, item, now);
            AddReminderIfNeeded(connection, transaction, item.Id, decisions[index], migrationState, capture, now);
        }

        using (var apply = connection.CreateCommand())
        {
            apply.Transaction = transaction;
            apply.CommandText = """
                UPDATE memo_parse_results
                SET status = 'Applied',
                    applied_at_utc = $now,
                    applied_memo_id = $memoId,
                    last_error = NULL,
                    updated_at_utc = $now
                WHERE id = $id AND status = 'AwaitingApply';
                """;
            apply.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            apply.Parameters.AddWithValue("$memoId", initial.Id.ToString("N"));
            apply.Parameters.AddWithValue("$id", parseResultId.ToString("N"));
            apply.ExecuteNonQuery();
        }

        using (var captureUpdate = connection.CreateCommand())
        {
            captureUpdate.Transaction = transaction;
            captureUpdate.CommandText = """
                UPDATE captures
                SET parse_status = 'Parsed', last_error = NULL
                WHERE id = $id;
                """;
            captureUpdate.Parameters.AddWithValue("$id", result.CaptureId.ToString("N"));
            captureUpdate.ExecuteNonQuery();
        }

        transaction.Commit();
        return MemoApplyOutcome.Applied;
    }

    private static bool LoadResult(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        out ResultRow result)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT capture_id, operation_version, status, candidates_json, applied_at_utc
            FROM memo_parse_results
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            result = null!;
            return false;
        }

        IReadOnlyList<MemoParseCandidate> candidates;
        try
        {
            candidates = JsonDefaults.Deserialize<List<MemoParseCandidate>>(reader.GetString(3))
                ?? new List<MemoParseCandidate>();
        }
        catch (System.Text.Json.JsonException)
        {
            candidates = Array.Empty<MemoParseCandidate>();
        }

        result = new ResultRow(
            Guid.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            Enum.Parse<MemoParseStatus>(reader.GetString(2), ignoreCase: false),
            candidates,
            reader.IsDBNull(4) ? null : SqliteMapping.ParseUtc(reader.GetString(4)));
        return true;
    }

    private static bool LoadCapture(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId,
        out CaptureRow capture)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT raw_text, captured_at_utc, captured_time_zone_id, parse_operation_version
            FROM captures
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", captureId.ToString("N"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            capture = null!;
            return false;
        }

        capture = new CaptureRow(
            Guid.Empty,
            reader.GetString(0),
            SqliteMapping.ParseUtc(reader.GetString(1)),
            reader.GetString(2),
            reader.GetInt32(3));
        return true;
    }

    private static Item? FindInitialMemo(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SqliteItemStore.SelectItems
            + " WHERE capture_id = $captureId ORDER BY created_at_utc ASC, rowid ASC LIMIT 1;";
        command.Parameters.AddWithValue("$captureId", captureId.ToString("N"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? SqliteItemStore.Map(reader) : null;
    }

    private static bool UpdateInitialMemo(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Item initial,
        MemoParseCandidate candidate,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE items
            SET content = $content,
                tags_json = $tags,
                priority = $priority,
                work_date = $workDate,
                work_start_time = $workStart,
                work_end_time = $workEnd,
                work_end_date = $workEndDate,
                work_precision = $workPrecision,
                work_time_text = $workTimeText,
                updated_at_utc = $updatedAt,
                row_version = row_version + 1
            WHERE id = $id
              AND row_version = $expectedVersion
              AND status = 'Open'
              AND human_revised = 0;
            """;
        command.Parameters.AddWithValue("$content", candidate.Content.Trim());
        command.Parameters.AddWithValue("$tags", JsonDefaults.Serialize(NormalizeTags(candidate.Tags)));
        command.Parameters.AddWithValue("$priority", (object?)candidate.Priority?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$workDate", candidate.WorkTime is null ? DBNull.Value : SqliteMapping.ToDb(candidate.WorkTime.Date));
        command.Parameters.AddWithValue("$workStart", SqliteMapping.ToDb(candidate.WorkTime?.Start));
        command.Parameters.AddWithValue("$workEnd", SqliteMapping.ToDb(candidate.WorkTime?.End));
        command.Parameters.AddWithValue("$workEndDate", SqliteMapping.ToDb(candidate.WorkTime?.EndDate));
        command.Parameters.AddWithValue("$workPrecision", (object?)candidate.WorkTime?.Precision.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$workTimeText", (object?)candidate.WorkTime?.OriginalText ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", SqliteMapping.ToUtcText(now));
        command.Parameters.AddWithValue("$id", initial.Id.ToString("N"));
        command.Parameters.AddWithValue("$expectedVersion", initial.RowVersion);
        return command.ExecuteNonQuery() == 1;
    }

    private static void AddReminderIfNeeded(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid itemId,
        MemoReminderDecision decision,
        string? migrationState,
        CaptureRow capture,
        DateTimeOffset now)
    {
        if (!decision.Allow || decision.TargetAtUtc is not { } target)
        {
            return;
        }

        // 只有明确的新录入 context 可以按“捕获时属于未来”补提醒。
        // created-raw 是迁移补出的原文：只保留仍有效的未来提醒，过期目标不补弹。
        // 缺失/未知状态不自动建计划，避免把来源判定问题变成历史提醒。
        if (migrationState == "current")
        {
            if (decision.Intent != MemoReminderIntent.Immediate && target <= capture.CapturedAtUtc)
            {
                return;
            }
        }
        else if (migrationState == "created-raw")
        {
            if (target <= now)
            {
                return;
            }
        }
        else
        {
            return;
        }

        using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT 1 FROM reminders WHERE item_id = $itemId AND enabled = 1;";
        exists.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
        if (exists.ExecuteScalar() is not null)
        {
            return;
        }

        SqliteItemStore.InsertReminder(
            connection,
            transaction,
            new ReminderPlan(
                Guid.NewGuid(),
                itemId,
                target,
                Enabled: true,
                PlanVersion: 1,
                CreatedAtUtc: now,
                UpdatedAtUtc: now,
                Source: "AIValidated", CapturedAtUtc: capture.CapturedAtUtc));
    }

    private static string? LoadMigrationState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid captureId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT state FROM migration_context WHERE capture_id = $captureId LIMIT 1;";
        command.Parameters.AddWithValue("$captureId", captureId.ToString("N"));
        return command.ExecuteScalar() as string;
    }

    private static void EnsureCapturedEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureRow capture,
        Item item,
        DateTimeOffset now)
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
                now,
                item.Content));
    }

    private static void MarkSkipped(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid parseResultId,
        string reason,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE memo_parse_results
            SET status = 'Skipped',
                last_error = $reason,
                updated_at_utc = $now
            WHERE id = $id AND status = 'AwaitingApply';
            """;
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
        command.Parameters.AddWithValue("$id", parseResultId.ToString("N"));
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags) => tags
        .Select(tag => tag.Trim())
        .Where(tag => tag.Length > 0)
        .Select(tag => tag.Length > 30 ? tag[..30] : tag)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(10)
        .ToArray();

    private sealed record ResultRow(
        Guid CaptureId,
        int OperationVersion,
        MemoParseStatus Status,
        IReadOnlyList<MemoParseCandidate> Candidates,
        DateTimeOffset? AppliedAtUtc);

    private sealed record CaptureRow(
        Guid Id,
        string RawText,
        DateTimeOffset CapturedAtUtc,
        string TimeZoneId,
        int OperationVersion);
}
