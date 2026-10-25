using MemoDesk.Core;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Time;
using MemoDesk.Core.Timeline;
using MemoDesk.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Infrastructure.Queries;

public enum StatusFilter
{
    Open = 0,
    Completed = 1,
    All = 2,
}

public enum MemoFilter
{
    Unprocessed = 0,
    Completed = 1,
    Archived = 2,
    All = 3,
}

public enum SearchStatusFilter
{
    Any = 0,
    Open = 1,
    Completed = 2,
    Archived = 3,
}

public sealed record TimelineEntry(
    Guid EventId,
    Guid? ItemId,
    ItemType? ItemType,
    TimelineEventType EventType,
    DateTimeOffset OccurredAtUtc,
    string Content,
    string DisplayTime);

public enum WorkRecordKind
{
    Captured = 0,
    Completed = 1,
    WorkOccurred = 2,
}

public sealed record WorkRecordEntry(
    Guid EventId,
    Item Item,
    WorkRecordKind Kind,
    DateTimeOffset OccurredAtUtc,
    string Content,
    string RawText,
    string DisplayTime,
    string Note);

public sealed record TodaySnapshot(
    IReadOnlyList<Item> TodayTodos,
    IReadOnlyList<Item> OverdueTodos,
    int UnscheduledCount,
    IReadOnlyList<TimelineEntry> Timeline,
    int UnprocessedMemoCount);

/// <summary>界面查询服务：只读组合查询，不承担业务写入规则。</summary>
public sealed class ItemQueryService
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public ItemQueryService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public TodaySnapshot GetTodaySnapshot(DateOnly today, DateTimeOffset nowUtc, TimeZoneInfo timeZone)
    {
        var openTodos = QueryItems("type = 'Todo' AND status = 'Open' AND status <> 'Deleted'");
        var reminderTimes = LoadActiveReminderTimes();

        var todayTodos = new List<Item>();
        foreach (var item in openTodos)
        {
            var dueToday = item.DueAtUtc is { } due && BusinessTime.DateOf(due, timeZone) == today;
            var reminderToday = reminderTimes.TryGetValue(item.Id, out var remindAt)
                && BusinessTime.DateOf(remindAt, timeZone) == today;

            if (item.ItemDate == today || dueToday || reminderToday)
            {
                todayTodos.Add(item);
            }
        }

        var todayIds = todayTodos.Select(item => item.Id).ToHashSet();
        var overdue = openTodos
            .Where(item => !todayIds.Contains(item.Id))
            .Where(item =>
                (item.ItemDate is { } date && date < today)
                || (item.DueAtUtc is { } due && BusinessTime.DateOf(due, timeZone) < today))
            .ToList();

        var overdueIds = overdue.Select(item => item.Id).ToHashSet();
        var unscheduledCount = openTodos.Count(item =>
            !todayIds.Contains(item.Id)
            && !overdueIds.Contains(item.Id)
            && item.ItemDate is null
            && item.DueAtUtc is null);

        return new TodaySnapshot(
            TodayTodos: todayTodos
                .OrderBy(item => item.DueAtUtc)
                .ThenBy(item => item.ItemDate)
                .ToList(),
            OverdueTodos: overdue
                .OrderBy(item => item.ItemDate)
                .ThenBy(item => item.DueAtUtc)
                .ToList(),
            UnscheduledCount: unscheduledCount,
            Timeline: GetTimeline(today, timeZone),
            UnprocessedMemoCount: CountMemos(today, ItemStatus.Open));
    }

    public IReadOnlyList<Item> GetTodos(StatusFilter filter)
    {
        var where = filter switch
        {
            StatusFilter.Open => "type = 'Todo' AND status = 'Open'",
            StatusFilter.Completed => "type = 'Todo' AND status = 'Completed'",
            _ => "type = 'Todo' AND status <> 'Deleted'",
        };

        return QueryItems(where + " ORDER BY CASE status WHEN 'Open' THEN 0 ELSE 1 END, item_date IS NULL, item_date, created_at_utc DESC");
    }

    public IReadOnlyList<Item> GetMemos(MemoFilter filter)
    {
        var where = filter switch
        {
            MemoFilter.Unprocessed => "type = 'Memo' AND status = 'Open'",
            MemoFilter.Completed => "type = 'Memo' AND status = 'Completed'",
            MemoFilter.Archived => "type = 'Memo' AND status = 'Archived'",
            _ => "type = 'Memo' AND status <> 'Deleted'",
        };

        return QueryItems(where + " ORDER BY memo_date DESC, created_at_utc DESC");
    }

    /// <summary>统一备忘投影：不再按 Todo/WorkLog/Memo 分类过滤，旧类型只作为历史来源。</summary>
    public IReadOnlyList<Item> GetUnifiedMemos(MemoFilter filter, string? search = null)
    {
        var conditions = new List<string>
        {
            filter switch
            {
                MemoFilter.Unprocessed => "items.status = 'Open'",
                MemoFilter.Completed => "items.status = 'Completed'",
                MemoFilter.Archived => "items.status = 'Archived'",
                _ => "items.status <> 'Deleted'",
            },
        };
        var parameters = new List<(string Name, object Value)>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            conditions.Add(
                """
                (
                    lower(items.content) LIKE $pattern ESCAPE '\'
                    OR EXISTS (
                        SELECT 1
                        FROM json_each(
                            CASE WHEN json_valid(items.tags_json) THEN items.tags_json ELSE '[]' END
                        ) AS tag
                        WHERE lower(tag.value) LIKE $pattern ESCAPE '\'
                    )
                    OR EXISTS (
                        SELECT 1
                        FROM captures c
                        WHERE c.id = items.capture_id
                          AND lower(IFNULL(c.raw_text, '')) LIKE $pattern ESCAPE '\'
                    )
                )
                """);
            parameters.Add(("$pattern", "%" + EscapeLike(search.Trim().ToLowerInvariant()) + "%"));
        }

        return QueryItems(
            string.Join(" AND ", conditions) + " ORDER BY items.created_at_utc DESC",
            parameters.ToArray());
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    public IReadOnlyList<Item> GetWidgetMemos(DateOnly today, bool includePrevious = false) =>
        QueryItems(
            includePrevious ? """
            status = 'Open'
            AND (COALESCE(memo_date, item_date) <= $date OR (memo_date IS NULL AND item_date IS NULL))
            ORDER BY created_at_utc ASC
            """ : """
            status = 'Open'
            AND (memo_date = $date OR (memo_date IS NULL AND item_date = $date))
            ORDER BY created_at_utc ASC
            """,
            ("$date", SqliteMapping.ToDb(today)));

    public IReadOnlyList<Item> GetWorkLogs(DateOnly from, DateOnly to) =>
        QueryItems(
            """
            type = 'WorkLog' AND status <> 'Deleted'
            AND (
                (work_date >= $from AND work_date <= $to)
                OR (work_end_date IS NOT NULL AND work_date <= $to AND work_end_date >= $from)
            )
            ORDER BY work_date DESC, work_start_time IS NULL, work_start_time DESC
            """,
            ("$from", SqliteMapping.ToDb(from)),
            ("$to", SqliteMapping.ToDb(to)));

    /// <summary>
    /// 工作记录投影：备忘录入、完成事件与旧 WorkLog 的发生时间合并展示。
    /// 事件使用自身时间筛选；旧 WorkLog 以 work_date/EndDate 与范围重叠判断。
    /// </summary>
    public IReadOnlyList<WorkRecordEntry> GetWorkRecords(
        DateOnly from,
        DateOnly to,
        TimeZoneInfo timeZone,
        string? search = null)
    {
        if (to < from)
        {
            return Array.Empty<WorkRecordEntry>();
        }

        var fromUtc = BusinessTime.StartOfDateUtc(from, timeZone);
        var toUtc = BusinessTime.StartOfDateUtc(to.AddDays(1), timeZone);
        var items = new SqliteItemStore(_connectionFactory);
        var result = new List<WorkRecordEntry>();

        using (var connection = _connectionFactory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT e.id, e.item_id, e.event_type, e.occurred_at_utc, e.content_snapshot, c.raw_text
                FROM timeline_events e
                LEFT JOIN items i ON i.id = e.item_id
                LEFT JOIN captures c ON c.id = i.capture_id
                WHERE e.event_type IN ('MemoCaptured', 'ItemCompleted')
                  AND e.occurred_at_utc >= $from AND e.occurred_at_utc < $to
                ORDER BY e.occurred_at_utc ASC, e.id ASC;
                """;
            command.Parameters.AddWithValue("$from", SqliteMapping.ToUtcText(fromUtc));
            command.Parameters.AddWithValue("$to", SqliteMapping.ToUtcText(toUtc));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                var itemId = Guid.Parse(reader.GetString(1));
                var item = items.GetById(itemId);
                if (item is null || item.Status == ItemStatus.Deleted)
                {
                    continue;
                }

                var eventType = reader.GetString(2);
                var occurredAt = SqliteMapping.ParseUtc(reader.GetString(3));
                var snapshot = reader.GetString(4);
                var rawText = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                var kind = eventType == "MemoCaptured" ? WorkRecordKind.Captured : WorkRecordKind.Completed;
                var content = kind == WorkRecordKind.Captured ? item.Content : snapshot;
                var note = BuildWorkRecordNote(kind, item, occurredAt, timeZone);

                result.Add(new WorkRecordEntry(
                    Guid.Parse(reader.GetString(0)),
                    item,
                    kind,
                    occurredAt,
                    content,
                    rawText,
                    BusinessTime.FormatLocal(occurredAt, timeZone, "HH:mm"),
                    note));
            }
        }

        foreach (var item in GetWorkLogs(from, to))
        {
            var workTime = item.WorkTime;
            var workDate = workTime?.Date ?? from;
            var occurredAt = ResolveWorkOccurredAt(workDate, workTime?.Start, from, timeZone, fromUtc);
            var capture = item.CaptureId is { } captureId ? GetCaptureRawText(captureId) : string.Empty;
            result.Add(new WorkRecordEntry(
                item.Id,
                item,
                WorkRecordKind.WorkOccurred,
                occurredAt,
                item.Content,
                capture,
                BuildWorkTimeLabel(
                    workTime?.Precision ?? WorkTimePrecision.Date,
                    workTime?.Start,
                    workTime?.End,
                    workTime?.EndDate,
                    workTime?.OriginalText,
                    workDate,
                    from),
                "工作记录"));
        }

        var entries = result
            .OrderBy(entry => entry.OccurredAtUtc)
            .ThenBy(entry => entry.EventId)
            .AsEnumerable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var query = search.Trim();
            entries = entries.Where(entry =>
                entry.Content.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.RawText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Note.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return entries.ToArray();
    }

    private static string BuildWorkRecordNote(
        WorkRecordKind kind,
        Item item,
        DateTimeOffset occurredAt,
        TimeZoneInfo timeZone)
    {
        if (kind == WorkRecordKind.Completed)
        {
            return "标记完成";
        }

        if (item.WorkTime is { } workTime)
        {
            var dateText = workTime.Date.ToString("MM-dd");
            return $"录入备忘 · 发生于 {dateText} {workTime.Start?.ToString("HH\\:mm") ?? workTime.OriginalText ?? string.Empty}".TrimEnd();
        }

        return "录入备忘";
    }

    private string GetCaptureRawText(Guid captureId)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT raw_text FROM captures WHERE id = $id;";
        command.Parameters.AddWithValue("$id", captureId.ToString("N"));
        return command.ExecuteScalar() as string ?? string.Empty;
    }

    public IReadOnlyList<Item> GetCompletedOn(DateOnly date, TimeZoneInfo timeZone)
    {
        var from = BusinessTime.StartOfDateUtc(date, timeZone);
        var to = BusinessTime.StartOfDateUtc(date.AddDays(1), timeZone);
        return QueryItems(
            "status = 'Completed' AND completed_at_utc >= $from AND completed_at_utc < $to AND type <> 'WorkLog'",
            ("$from", SqliteMapping.ToUtcText(from)),
            ("$to", SqliteMapping.ToUtcText(to)));
    }

    public IReadOnlyList<TimelineEntry> GetTimeline(DateOnly date, TimeZoneInfo timeZone)
    {
        var from = BusinessTime.StartOfDateUtc(date, timeZone);
        var to = BusinessTime.StartOfDateUtc(date.AddDays(1), timeZone);
        var result = new List<TimelineEntry>();

        using var connection = _connectionFactory.Create();

        // 工作记录以事项本身为唯一来源：编辑日期/删除后自动反映；跨日区间与当天重叠即纳入。
        using (var workCommand = connection.CreateCommand())
        {
            workCommand.CommandText = """
                SELECT id, content, work_date, work_start_time, work_end_time, work_end_date,
                       work_precision, work_time_text
                FROM items
                WHERE type = 'WorkLog' AND status <> 'Deleted'
                  AND (
                       work_date = $date
                       OR (work_end_date IS NOT NULL AND work_date <= $date AND work_end_date >= $date)
                  );
                """;
            workCommand.Parameters.AddWithValue("$date", SqliteMapping.ToDb(date));

            using var reader = workCommand.ExecuteReader();
            while (reader.Read())
            {
                var itemId = Guid.Parse(reader.GetString(0));
                var content = reader.GetString(1);
                var workDate = SqliteMapping.ParseDateOrNull(reader.IsDBNull(2) ? null : reader.GetString(2)) ?? date;
                var start = SqliteMapping.ParseTimeOrNull(reader.IsDBNull(3) ? null : reader.GetString(3));
                var end = SqliteMapping.ParseTimeOrNull(reader.IsDBNull(4) ? null : reader.GetString(4));
                var endDate = SqliteMapping.ParseDateOrNull(reader.IsDBNull(5) ? null : reader.GetString(5));
                var precision = reader.IsDBNull(6)
                    ? WorkTimePrecision.Date
                    : Enum.Parse<WorkTimePrecision>(reader.GetString(6), ignoreCase: false);
                var originalText = reader.IsDBNull(7) ? null : reader.GetString(7);

                var occurredAt = ResolveWorkOccurredAt(workDate, start, date, timeZone, from);
                var display = BuildWorkTimeLabel(precision, start, end, endDate, originalText, workDate, date);

                result.Add(new TimelineEntry(
                    EventId: itemId,
                    ItemId: itemId,
                    ItemType: ItemType.WorkLog,
                    EventType: TimelineEventType.WorkOccurred,
                    OccurredAtUtc: occurredAt,
                    Content: content,
                    DisplayTime: display));
            }
        }

        using (var eventCommand = connection.CreateCommand())
        {
            eventCommand.CommandText = """
                SELECT e.id, e.item_id, i.type, e.occurred_at_utc, e.content_snapshot
                FROM timeline_events e
                LEFT JOIN items i ON i.id = e.item_id
                WHERE e.event_type = 'ItemCompleted'
                  AND e.occurred_at_utc >= $from AND e.occurred_at_utc < $to;
                """;
            eventCommand.Parameters.AddWithValue("$from", SqliteMapping.ToUtcText(from));
            eventCommand.Parameters.AddWithValue("$to", SqliteMapping.ToUtcText(to));

            using var reader = eventCommand.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new TimelineEntry(
                    EventId: Guid.Parse(reader.GetString(0)),
                    ItemId: reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                    ItemType: reader.IsDBNull(2) ? null : Enum.Parse<ItemType>(reader.GetString(2), ignoreCase: false),
                    EventType: TimelineEventType.ItemCompleted,
                    OccurredAtUtc: SqliteMapping.ParseUtc(reader.GetString(3)),
                    Content: reader.GetString(4),
                    DisplayTime: BusinessTime.FormatLocal(
                        SqliteMapping.ParseUtc(reader.GetString(3)), timeZone, "HH:mm")));
            }
        }

        return result
            .OrderBy(entry => entry.OccurredAtUtc)
            .ToArray();
    }

    private static DateTimeOffset ResolveWorkOccurredAt(
        DateOnly workDate,
        TimeOnly? start,
        DateOnly viewDate,
        TimeZoneInfo timeZone,
        DateTimeOffset dayStart)
    {
        if (workDate < viewDate)
        {
            // 跨日区间从更早开始：按当天起点排序，不伪造具体分钟。
            return dayStart;
        }

        return start is { } time ? BusinessTime.Combine(workDate, time, timeZone) : dayStart;
    }

    private static string BuildWorkTimeLabel(
        WorkTimePrecision precision,
        TimeOnly? start,
        TimeOnly? end,
        DateOnly? endDate,
        string? originalText,
        DateOnly workDate,
        DateOnly viewDate)
    {
        switch (precision)
        {
            case WorkTimePrecision.Exact when start is { } exact:
                return exact.ToString("HH:mm");
            case WorkTimePrecision.Range when start is { } rangeStart && end is { } rangeEnd:
                var crossDay = (endDate is { } endDay && endDay > workDate) || workDate < viewDate;
                return crossDay
                    ? $"跨日 {rangeStart:HH\\:mm}–{rangeEnd:HH\\:mm}"
                    : $"{rangeStart:HH\\:mm}–{rangeEnd:HH\\:mm}";
            case WorkTimePrecision.Period:
                return string.IsNullOrWhiteSpace(originalText) ? "时段" : originalText!;
            default:
                return "时间未细分";
        }
    }

    public int CountMemos(DateOnly date, ItemStatus status)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM items WHERE type = 'Memo' AND status = $status AND memo_date = $date;";
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$date", SqliteMapping.ToDb(date));
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<Item> QueryItems(string whereAndOrder, params (string Name, object Value)[] parameters)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SqliteItemStore.SelectItems + " WHERE " + whereAndOrder;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = new List<Item>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(SqliteItemStore.Map(reader));
        }

        return result;
    }

    private Dictionary<Guid, DateTimeOffset> LoadActiveReminderTimes()
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id, reminder_at_utc FROM reminders WHERE enabled = 1;";

        var result = new Dictionary<Guid, DateTimeOffset>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[Guid.Parse(reader.GetString(0))] = SqliteMapping.ParseUtc(reader.GetString(1));
        }

        return result;
    }
}
