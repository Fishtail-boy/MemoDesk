using MemoDesk.Core;
using MemoDesk.Core.Drafting;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Infrastructure.Storage;

public sealed class SqliteParseDraftRepository : IParseDraftRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteParseDraftRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public void Add(ParseDraft draft)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO parse_drafts (
                id, capture_id, version, status, candidates_json, raw_ai_json,
                model, prompt_version, schema_version, attempt_count, last_error,
                created_at_utc, updated_at_utc, edit_state_json, edit_version,
                operation_version, applied_at_utc, applied_memo_id)
            VALUES (
                $id, $captureId, $version, $status, $candidates, $rawAi,
                $model, $promptVersion, $schemaVersion, $attemptCount, $lastError,
                $createdAt, $updatedAt, $editState, $editVersion,
                $operationVersion, $appliedAt, $appliedMemoId);
            """;
        command.Parameters.AddWithValue("$id", draft.Id.ToString("N"));
        command.Parameters.AddWithValue("$captureId", draft.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$version", draft.Version);
        command.Parameters.AddWithValue("$status", draft.Status.ToString());
        command.Parameters.AddWithValue("$candidates", JsonDefaults.Serialize(draft.Candidates));
        command.Parameters.AddWithValue("$rawAi", (object?)draft.RawAiJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)draft.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$promptVersion", (object?)draft.PromptVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$schemaVersion", (object?)draft.SchemaVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$attemptCount", draft.AttemptCount);
        command.Parameters.AddWithValue("$lastError", (object?)draft.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", SqliteMapping.ToUtcText(draft.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedAt", SqliteMapping.ToUtcText(draft.UpdatedAtUtc));
        command.Parameters.AddWithValue("$editState", (object?)draft.EditStateJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$editVersion", draft.EditVersion);
        command.Parameters.AddWithValue("$operationVersion", draft.OperationVersion);
        command.Parameters.AddWithValue("$appliedAt", SqliteMapping.ToDb(draft.AppliedAtUtc));
        command.Parameters.AddWithValue("$appliedMemoId", (object?)draft.AppliedMemoId?.ToString("N") ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public ParseDraft? GetById(Guid id)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public ParseDraft? GetLatestForCapture(Guid captureId)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE capture_id = $captureId ORDER BY version DESC LIMIT 1;";
        command.Parameters.AddWithValue("$captureId", captureId.ToString("N"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<ParseDraft> GetByStatus(DraftStatus status)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE status = $status ORDER BY created_at_utc ASC;";
        command.Parameters.AddWithValue("$status", status.ToString());

        var result = new List<ParseDraft>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public int CountByStatus(DraftStatus status)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM parse_drafts WHERE status = $status;";
        command.Parameters.AddWithValue("$status", status.ToString());
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool SaveEditState(Guid draftId, int expectedEditVersion, string editStateJson, DateTimeOffset updatedAt)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE parse_drafts
            SET edit_state_json = $editState,
                edit_version = edit_version + 1,
                updated_at_utc = $updatedAt
            WHERE id = $id
              AND status = 'AwaitingConfirmation'
              AND edit_version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$editState", editStateJson);
        command.Parameters.AddWithValue("$updatedAt", SqliteMapping.ToUtcText(updatedAt));
        command.Parameters.AddWithValue("$id", draftId.ToString("N"));
        command.Parameters.AddWithValue("$expectedVersion", expectedEditVersion);
        return command.ExecuteNonQuery() == 1;
    }

    public void SaveParsingResult(
        Guid draftId,
        DraftStatus status,
        IReadOnlyList<DraftCandidate> candidates,
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
            UPDATE parse_drafts
            SET status = $status,
                candidates_json = $candidates,
                raw_ai_json = $rawAi,
                model = $model,
                prompt_version = $promptVersion,
                schema_version = $schemaVersion,
                attempt_count = $attemptCount,
                last_error = $lastError,
                edit_state_json = NULL,
                edit_version = 0,
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
        command.Parameters.AddWithValue("$id", draftId.ToString("N"));
        command.ExecuteNonQuery();
    }

    public void UpdateStatus(Guid draftId, DraftStatus status, string? error, DateTimeOffset updatedAt)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE parse_drafts
            SET status = $status, last_error = $lastError, updated_at_utc = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$lastError", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", SqliteMapping.ToUtcText(updatedAt));
        command.Parameters.AddWithValue("$id", draftId.ToString("N"));
        command.ExecuteNonQuery();
    }

    private const string SelectColumns = """
        SELECT id, capture_id, version, status, candidates_json, raw_ai_json,
               model, prompt_version, schema_version, attempt_count, last_error,
               created_at_utc, updated_at_utc, edit_state_json, edit_version,
               operation_version, applied_at_utc, applied_memo_id
        FROM parse_drafts
        """;

    private static ParseDraft Map(SqliteDataReader reader)
    {
        var candidatesJson = reader.GetString(4);
        IReadOnlyList<DraftCandidate> candidates;
        try
        {
            candidates = JsonDefaults.Deserialize<List<DraftCandidate>>(candidatesJson) ?? new List<DraftCandidate>();
        }
        catch (System.Text.Json.JsonException)
        {
            candidates = Array.Empty<DraftCandidate>();
        }

        return new ParseDraft(
            Id: Guid.Parse(reader.GetString(0)),
            CaptureId: Guid.Parse(reader.GetString(1)),
            Version: reader.GetInt32(2),
            Status: Enum.Parse<DraftStatus>(reader.GetString(3), ignoreCase: false),
            Candidates: candidates,
            RawAiJson: reader.IsDBNull(5) ? null : reader.GetString(5),
            Model: reader.IsDBNull(6) ? null : reader.GetString(6),
            PromptVersion: reader.IsDBNull(7) ? null : reader.GetString(7),
            SchemaVersion: reader.IsDBNull(8) ? null : reader.GetString(8),
            AttemptCount: reader.GetInt32(9),
            LastError: reader.IsDBNull(10) ? null : reader.GetString(10),
            CreatedAtUtc: SqliteMapping.ParseUtc(reader.GetString(11)),
            UpdatedAtUtc: SqliteMapping.ParseUtc(reader.GetString(12)),
            EditStateJson: reader.IsDBNull(13) ? null : reader.GetString(13),
            EditVersion: reader.GetInt32(14),
            OperationVersion: reader.GetInt32(15),
            AppliedAtUtc: reader.IsDBNull(16) ? null : SqliteMapping.ParseUtc(reader.GetString(16)),
            AppliedMemoId: reader.IsDBNull(17) ? null : Guid.Parse(reader.GetString(17)));
    }
}
