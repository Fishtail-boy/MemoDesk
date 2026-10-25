using MemoDesk.Core;
using MemoDesk.Core.Parsing;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Infrastructure.Storage;

public sealed class SqliteMemoParseRepository : IMemoParseRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteMemoParseRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public void Add(MemoParseRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memo_parse_results (
                id, capture_id, operation_version, version, status, candidates_json,
                raw_ai_json, model, prompt_version, schema_version, attempt_count,
                last_error, applied_at_utc, applied_memo_id, created_at_utc, updated_at_utc)
            VALUES (
                $id, $captureId, $operationVersion, $version, $status, $candidates,
                $rawAi, $model, $promptVersion, $schemaVersion, $attemptCount,
                $lastError, $appliedAt, $appliedMemoId, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString("N"));
        command.Parameters.AddWithValue("$captureId", record.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$operationVersion", record.OperationVersion);
        command.Parameters.AddWithValue("$version", record.Version);
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$candidates", JsonDefaults.Serialize(record.Candidates));
        command.Parameters.AddWithValue("$rawAi", (object?)record.RawAiJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)record.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$promptVersion", (object?)record.PromptVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$schemaVersion", (object?)record.SchemaVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$attemptCount", record.AttemptCount);
        command.Parameters.AddWithValue("$lastError", (object?)record.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$appliedAt", SqliteMapping.ToDb(record.AppliedAtUtc));
        command.Parameters.AddWithValue("$appliedMemoId", (object?)record.AppliedMemoId?.ToString("N") ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", SqliteMapping.ToUtcText(record.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedAt", SqliteMapping.ToUtcText(record.UpdatedAtUtc));
        command.ExecuteNonQuery();
    }

    public MemoParseRecord? GetById(Guid id)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public MemoParseRecord? GetLatestForCapture(Guid captureId)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE capture_id = $captureId ORDER BY version DESC LIMIT 1;";
        command.Parameters.AddWithValue("$captureId", captureId.ToString("N"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<MemoParseRecord> GetByStatus(MemoParseStatus status, int limit)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE status = $status ORDER BY created_at_utc ASC, id ASC LIMIT $limit;";
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<MemoParseRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public IReadOnlyList<MemoParseRecord> GetByStatusAfter(
        MemoParseStatus status,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterId,
        int limit)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + """
             WHERE status = $status
               AND (created_at_utc > $afterAt
                    OR (created_at_utc = $afterAt AND id > $afterId))
             ORDER BY created_at_utc ASC, id ASC
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$afterAt", SqliteMapping.ToUtcText(afterCreatedAtUtc));
        command.Parameters.AddWithValue("$afterId", afterId.ToString("N"));
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<MemoParseRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public void SaveResult(
        Guid id,
        MemoParseStatus status,
        IReadOnlyList<MemoParseCandidate> candidates,
        string? rawAiJson,
        string? model,
        string? promptVersion,
        string? schemaVersion,
        int attemptCount,
        string? error,
        DateTimeOffset updatedAt)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memo_parse_results
            SET status = $status,
                candidates_json = $candidates,
                raw_ai_json = $rawAi,
                model = $model,
                prompt_version = $promptVersion,
                schema_version = $schemaVersion,
                attempt_count = $attemptCount,
                last_error = $lastError,
                updated_at_utc = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$candidates", JsonDefaults.Serialize(candidates));
        command.Parameters.AddWithValue("$rawAi", (object?)rawAiJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
        command.Parameters.AddWithValue("$promptVersion", (object?)promptVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$schemaVersion", (object?)schemaVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$attemptCount", attemptCount);
        command.Parameters.AddWithValue("$lastError", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", SqliteMapping.ToUtcText(updatedAt));
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.ExecuteNonQuery();
    }

    public void UpdateStatus(Guid id, MemoParseStatus status, string? error, DateTimeOffset updatedAt)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memo_parse_results
            SET status = $status, last_error = $lastError, updated_at_utc = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$lastError", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", SqliteMapping.ToUtcText(updatedAt));
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.ExecuteNonQuery();
    }

    public void MarkSuperseded(
        Guid captureId,
        int operationVersion,
        string reason,
        DateTimeOffset updatedAt)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memo_parse_results
            SET status = 'Superseded',
                last_error = $reason,
                updated_at_utc = $updatedAt
            WHERE capture_id = $captureId
              AND operation_version <= $operationVersion
              AND status IN ('Parsing', 'AwaitingApply', 'Failed');
            """;
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$updatedAt", SqliteMapping.ToUtcText(updatedAt));
        command.Parameters.AddWithValue("$captureId", captureId.ToString("N"));
        command.Parameters.AddWithValue("$operationVersion", operationVersion);
        command.ExecuteNonQuery();
    }

    private const string SelectColumns = """
        SELECT id, capture_id, operation_version, version, status, candidates_json,
               raw_ai_json, model, prompt_version, schema_version, attempt_count,
               last_error, applied_at_utc, applied_memo_id, created_at_utc, updated_at_utc
        FROM memo_parse_results
        """;

    private static MemoParseRecord Map(SqliteDataReader reader)
    {
        IReadOnlyList<MemoParseCandidate> candidates;
        try
        {
            candidates = JsonDefaults.Deserialize<List<MemoParseCandidate>>(reader.GetString(5))
                ?? new List<MemoParseCandidate>();
        }
        catch (System.Text.Json.JsonException)
        {
            candidates = Array.Empty<MemoParseCandidate>();
        }

        return new MemoParseRecord(
            Id: Guid.Parse(reader.GetString(0)),
            CaptureId: Guid.Parse(reader.GetString(1)),
            OperationVersion: reader.GetInt32(2),
            Version: reader.GetInt32(3),
            Status: Enum.Parse<MemoParseStatus>(reader.GetString(4), ignoreCase: false),
            Candidates: candidates,
            RawAiJson: reader.IsDBNull(6) ? null : reader.GetString(6),
            Model: reader.IsDBNull(7) ? null : reader.GetString(7),
            PromptVersion: reader.IsDBNull(8) ? null : reader.GetString(8),
            SchemaVersion: reader.IsDBNull(9) ? null : reader.GetString(9),
            AttemptCount: reader.GetInt32(10),
            LastError: reader.IsDBNull(11) ? null : reader.GetString(11),
            AppliedAtUtc: reader.IsDBNull(12) ? null : SqliteMapping.ParseUtc(reader.GetString(12)),
            AppliedMemoId: reader.IsDBNull(13) ? null : Guid.Parse(reader.GetString(13)),
            CreatedAtUtc: SqliteMapping.ParseUtc(reader.GetString(14)),
            UpdatedAtUtc: SqliteMapping.ParseUtc(reader.GetString(15)));
    }
}
