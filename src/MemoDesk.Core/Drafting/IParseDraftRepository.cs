namespace MemoDesk.Core.Drafting;

public interface IParseDraftRepository
{
    void Add(ParseDraft draft);

    ParseDraft? GetById(Guid id);

    ParseDraft? GetLatestForCapture(Guid captureId);

    IReadOnlyList<ParseDraft> GetByStatus(DraftStatus status);

    int CountByStatus(DraftStatus status);

    /// <summary>
    /// 保存草稿编辑状态（原始输入 + 脏字段），使用 edit_version 乐观并发；
    /// 返回 false 表示草稿已被确认/丢弃或被其他编辑更新。
    /// </summary>
    bool SaveEditState(Guid draftId, int expectedEditVersion, string editStateJson, DateTimeOffset updatedAt);

    /// <summary>解析结束时写入完整结果并更新状态。</summary>
    void SaveParsingResult(
        Guid draftId,
        DraftStatus status,
        IReadOnlyList<DraftCandidate> candidates,
        string? rawAiJson,
        string? model,
        string? promptVersion,
        string? schemaVersion,
        int attemptCount,
        string? error,
        DateTimeOffset updatedAt);

    void UpdateStatus(Guid draftId, DraftStatus status, string? error, DateTimeOffset updatedAt);
}
