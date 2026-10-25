using MemoDesk.Core.Capturing;
using MemoDesk.Core.Drafting;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Timeline;
using MemoDesk.Infrastructure.Queries;
using MemoDesk.Infrastructure.Storage;
using MemoDesk.Infrastructure.Storage.Migrations;

namespace MemoDesk.Infrastructure.Tests;

/// <summary>测试用完整数据库：迁移到最新版本并提供各仓储。</summary>
internal sealed class TestDatabase : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 21, 2, 0, 0, TimeSpan.Zero);

    private readonly TempDatabase _database = new();

    public TestDatabase()
    {
        new MigrationRunner(_database.Factory).Migrate();
        Captures = new SqliteCaptureRepository(_database.Factory);
        Drafts = new SqliteParseDraftRepository(_database.Factory);
        Items = new SqliteItemStore(_database.Factory);
        Reminders = new SqliteReminderRepository(_database.Factory);
        Queries = new ItemQueryService(_database.Factory);
        Search = new SearchService(_database.Factory);
        SettingsStore = new SqliteSettingsStore(_database.Factory);
    }

    public SqliteConnectionFactory Factory => _database.Factory;

    public SqliteCaptureRepository Captures { get; }

    public SqliteParseDraftRepository Drafts { get; }

    public SqliteItemStore Items { get; }

    public SqliteReminderRepository Reminders { get; }

    public ItemQueryService Queries { get; }

    public SearchService Search { get; }

    public SqliteSettingsStore SettingsStore { get; }

    public Capture SeedCapture(string rawText = "原始输入")
    {
        var capture = new Capture(
            Guid.NewGuid(), rawText, Now, "Asia/Shanghai", CaptureParseStatus.Pending, null, Now);
        Captures.Add(capture);
        return capture;
    }

    /// <summary>通过“草稿确认”路径写入一条事项，返回落库后的事项。</summary>
    public Item SeedItem(Item item, ReminderPlan? reminder = null, TimelineEvent? timeline = null, Capture? capture = null)
    {
        var captureRecord = capture ?? SeedCapture();
        var prepared = item with
        {
            CaptureId = captureRecord.Id,
            CandidateSourceId = item.CandidateSourceId ?? Guid.NewGuid(),
        };

        var draft = new ParseDraft(
            Guid.NewGuid(), captureRecord.Id, 1, DraftStatus.AwaitingConfirmation,
            Array.Empty<DraftCandidate>(), null, null, "p1", "v1", 0, null, Now, Now);
        Drafts.Add(draft);

        var reminders = reminder is null
            ? Array.Empty<ReminderPlan>()
            : new[] { reminder with { ItemId = prepared.Id } };
        var events = timeline is null
            ? Array.Empty<TimelineEvent>()
            : new[] { timeline with { ItemId = prepared.Id } };

        var outcome = Items.ConfirmDraft(draft.Id, 1, new[] { prepared }, reminders, events, Now);
        Assert.Equal(DraftConfirmationOutcome.Confirmed, outcome);
        return prepared;
    }

    public static Item Todo(
        string content,
        DateOnly? itemDate = null,
        DateTimeOffset? dueAt = null,
        ItemStatus status = ItemStatus.Open) => new()
    {
        Id = Guid.NewGuid(),
        Type = ItemType.Todo,
        Content = content,
        Status = status,
        ItemDate = itemDate,
        DueAtUtc = dueAt,
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
    };

    public static Item Memo(
        string content,
        DateOnly memoDate,
        ItemStatus status = ItemStatus.Open,
        IReadOnlyList<string>? tags = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = ItemType.Memo,
        Content = content,
        Status = status,
        MemoDate = memoDate,
        Tags = tags ?? Array.Empty<string>(),
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
    };

    public static Item WorkLog(
        string content,
        DateOnly date,
        TimeOnly? start = null,
        DateOnly? endDate = null,
        WorkTimePrecision precision = WorkTimePrecision.Date) => new()
    {
        Id = Guid.NewGuid(),
        Type = ItemType.WorkLog,
        Content = content,
        Status = ItemStatus.Open,
        WorkTime = new WorkTimeInfo(date, start, null, null, precision, endDate),
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
    };

    public static ReminderPlan Reminder(Guid itemId, DateTimeOffset at) => new(
        Guid.NewGuid(), itemId, at, true, 1, Now, Now);

    public void Dispose() => _database.Dispose();
}
