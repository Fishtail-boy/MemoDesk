namespace MemoDesk.Core.Capturing;

public interface ICaptureRepository
{
    /// <summary>持久化一条原文。调用方负责在成功后才收起输入界面。</summary>
    void Add(Capture capture);

    Capture? GetById(Guid id);

    void UpdateParseStatus(Guid id, CaptureParseStatus status, string? lastError);

    /// <summary>推进解析操作版本并置为待解析；返回新版本，原文不存在返回 0。</summary>
    int IncrementParseOperationVersion(Guid id);

    IReadOnlyList<Capture> GetByStatus(CaptureParseStatus status, int limit);

    /// <summary>按 (recorded_at_utc, id) 稳定游标读取下一批，避免状态更新造成 OFFSET 跳行。</summary>
    IReadOnlyList<Capture> GetByStatusAfter(
        CaptureParseStatus status,
        DateTimeOffset afterRecordedAtUtc,
        Guid afterId,
        int limit);
}
