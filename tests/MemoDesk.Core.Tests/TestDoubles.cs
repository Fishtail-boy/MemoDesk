using MemoDesk.Core.Capturing;

namespace MemoDesk.Core.Tests;

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}

internal sealed class InMemoryCaptureRepository : ICaptureRepository
{
    public List<Capture> Items { get; } = new();

    public void Add(Capture capture) => Items.Add(capture);

    public Capture? GetById(Guid id) => Items.FirstOrDefault(c => c.Id == id);

    public void UpdateParseStatus(Guid id, CaptureParseStatus status, string? lastError)
    {
        var index = Items.FindIndex(c => c.Id == id);
        if (index >= 0)
        {
            Items[index] = Items[index] with { ParseStatus = status, LastError = lastError };
        }
    }

    public int IncrementParseOperationVersion(Guid id)
    {
        var index = Items.FindIndex(c => c.Id == id);
        if (index < 0)
        {
            return 0;
        }

        var version = Items[index].ParseOperationVersion + 1;
        Items[index] = Items[index] with
        {
            ParseOperationVersion = version,
            ParseStatus = CaptureParseStatus.Pending,
            LastError = null,
        };
        return version;
    }

    public IReadOnlyList<Capture> GetByStatus(CaptureParseStatus status, int limit) =>
        Items.Where(c => c.ParseStatus == status).Take(limit).ToArray();

    public IReadOnlyList<Capture> GetByStatusAfter(
        CaptureParseStatus status,
        DateTimeOffset afterRecordedAtUtc,
        Guid afterId,
        int limit) =>
        Items.Where(c => c.ParseStatus == status)
            .Where(c => c.RecordedAtUtc > afterRecordedAtUtc
                || (c.RecordedAtUtc == afterRecordedAtUtc && c.Id.CompareTo(afterId) > 0))
            .OrderBy(c => c.RecordedAtUtc)
            .ThenBy(c => c.Id)
            .Take(limit)
            .ToArray();
}
