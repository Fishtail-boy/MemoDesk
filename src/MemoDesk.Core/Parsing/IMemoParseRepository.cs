namespace MemoDesk.Core.Parsing;

/// <summary>B 版整理结果的持久化入口；结果先保存，再由应用事务消费。</summary>
public interface IMemoParseRepository
{
    void Add(MemoParseRecord record);

    MemoParseRecord? GetById(Guid id);

    MemoParseRecord? GetLatestForCapture(Guid captureId);

    IReadOnlyList<MemoParseRecord> GetByStatus(MemoParseStatus status, int limit);

    /// <summary>按 (created_at_utc, id) 稳定游标读取下一批。</summary>
    IReadOnlyList<MemoParseRecord> GetByStatusAfter(
        MemoParseStatus status,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterId,
        int limit);

    void SaveResult(
        Guid id,
        MemoParseStatus status,
        IReadOnlyList<MemoParseCandidate> candidates,
        string? rawAiJson,
        string? model,
        string? promptVersion,
        string? schemaVersion,
        int attemptCount,
        string? error,
        DateTimeOffset updatedAt);

    void UpdateStatus(Guid id, MemoParseStatus status, string? error, DateTimeOffset updatedAt);

    void MarkSuperseded(Guid captureId, int operationVersion, string reason, DateTimeOffset updatedAt);
}
