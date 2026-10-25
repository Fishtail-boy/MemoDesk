using MemoDesk.Core.Capturing;

namespace MemoDesk.Infrastructure.Storage;

/// <summary>captures 表的 SQLite 实现。时间以 ISO 8601（"O"）存储，状态以枚举名存储。</summary>
public sealed class SqliteCaptureRepository : ICaptureRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteCaptureRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public void Add(Capture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
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

    public Capture? GetById(Guid id)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, raw_text, captured_at_utc, captured_time_zone_id,
                   parse_status, last_error, recorded_at_utc, parse_operation_version
            FROM captures
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("N"));

        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void UpdateParseStatus(Guid id, CaptureParseStatus status, string? lastError)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE captures
            SET parse_status = $status, last_error = $lastError
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$lastError", (object?)lastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.ExecuteNonQuery();
    }

    public int IncrementParseOperationVersion(Guid id)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE captures
                SET parse_operation_version = parse_operation_version + 1,
                    parse_status = 'Pending',
                    last_error = NULL
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", id.ToString("N"));
            if (update.ExecuteNonQuery() != 1)
            {
                return 0;
            }
        }

        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT parse_operation_version FROM captures WHERE id = $id;";
        select.Parameters.AddWithValue("$id", id.ToString("N"));
        var version = Convert.ToInt32(select.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        transaction.Commit();
        return version;
    }

    public IReadOnlyList<Capture> GetByStatus(CaptureParseStatus status, int limit)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, raw_text, captured_at_utc, captured_time_zone_id,
                   parse_status, last_error, recorded_at_utc, parse_operation_version
            FROM captures
            WHERE parse_status = $status
            ORDER BY recorded_at_utc ASC, id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<Capture>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public IReadOnlyList<Capture> GetByStatusAfter(
        CaptureParseStatus status,
        DateTimeOffset afterRecordedAtUtc,
        Guid afterId,
        int limit)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, raw_text, captured_at_utc, captured_time_zone_id,
                   parse_status, last_error, recorded_at_utc, parse_operation_version
            FROM captures
            WHERE parse_status = $status
              AND (recorded_at_utc > $afterAt
                   OR (recorded_at_utc = $afterAt AND id > $afterId))
            ORDER BY recorded_at_utc ASC, id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$afterAt", SqliteMapping.ToUtcText(afterRecordedAtUtc));
        command.Parameters.AddWithValue("$afterId", afterId.ToString("N"));
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<Capture>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(Map(reader));
        }

        return result;
    }

    private static Capture Map(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        Id: Guid.Parse(reader.GetString(0)),
        RawText: reader.GetString(1),
        CapturedAtUtc: SqliteMapping.ParseUtc(reader.GetString(2)),
        CapturedTimeZoneId: reader.GetString(3),
        ParseStatus: Enum.Parse<CaptureParseStatus>(reader.GetString(4), ignoreCase: false),
        LastError: reader.IsDBNull(5) ? null : reader.GetString(5),
        RecordedAtUtc: SqliteMapping.ParseUtc(reader.GetString(6)),
        ParseOperationVersion: reader.GetInt32(7));
}
