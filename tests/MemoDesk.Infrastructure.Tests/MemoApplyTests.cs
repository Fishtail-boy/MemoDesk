using MemoDesk.Core;
using MemoDesk.Core.Capturing;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Parsing;
using MemoDesk.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemoDesk.Infrastructure.Tests;

public sealed class MemoApplyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Apply_UpdatesInitialMemoAndAddsSplitMemoWithReminders()
    {
        using var db = new TestDatabase();
        var (capture, initial) = Capture(db, "2026-10-25T15:00:00+08:00 提交报表；2026-10-25T18:00:00+08:00 联系张工");
        var result = AddResult(db, capture, new[]
        {
            new MemoParseCandidate
            {
                SourceId = StableId.Candidate(capture.Id, 1, 0),
                Content = "提交报表",
                SourceText = "2026-10-25T15:00:00+08:00 提交报表",
                RemindText = "2026-10-25T15:00:00+08:00",
                RemindAtUtc = Now.AddHours(5),
                Tags = { "工作" },
            },
            new MemoParseCandidate
            {
                SourceId = StableId.Candidate(capture.Id, 1, 1),
                Content = "联系张工",
                SourceText = "2026-10-25T18:00:00+08:00 联系张工",
                RemindText = "2026-10-25T18:00:00+08:00",
                RemindAtUtc = Now.AddHours(8),
            },
        });

        var outcome = new SqliteMemoApplyStore(db.Factory).Apply(result.Id, Now.AddMinutes(1));

        Assert.Equal(MemoApplyOutcome.Applied, outcome);
        var updated = db.Items.GetById(initial.Id)!;
        Assert.Equal("提交报表", updated.Content);
        Assert.Equal(2, updated.RowVersion);
        Assert.Contains("工作", updated.Tags);

        var all = db.Queries.QueryItems("capture_id = $captureId ORDER BY created_at_utc, rowid", ("$captureId", capture.Id.ToString("N")));
        Assert.Equal(2, all.Count);
        Assert.Contains(all, item => item.Content == "联系张工");

        Assert.NotNull(db.Reminders.GetActiveForItem(initial.Id));
        Assert.NotNull(db.Reminders.GetActiveForItem(all.Single(item => item.Content == "联系张工").Id));
        Assert.Equal("Applied", dbFactoryParseStatus(db, result.Id));
        Assert.Equal(CaptureParseStatus.Parsed, db.Captures.GetById(capture.Id)!.ParseStatus);
    }

    [Fact]
    public void Apply_IsIdempotentForAlreadyAppliedResult()
    {
        using var db = new TestDatabase();
        var (capture, initial) = Capture(db, "同一条");
        var result = AddResult(db, capture, new[]
        {
            new MemoParseCandidate { SourceId = StableId.Candidate(capture.Id, 1, 0), Content = "整理后" },
        });

        var store = new SqliteMemoApplyStore(db.Factory);
        Assert.Equal(MemoApplyOutcome.Applied, store.Apply(result.Id, Now));
        Assert.Equal(MemoApplyOutcome.AlreadyApplied, store.Apply(result.Id, Now.AddMinutes(1)));
        Assert.Equal("整理后", db.Items.GetById(initial.Id)!.Content);
    }

    [Fact]
    public void Apply_HumanRevisedInitialMemoIsSkipped()
    {
        using var db = new TestDatabase();
        var (capture, initial) = Capture(db, "原文");
        var result = AddResult(db, capture, new[]
        {
            new MemoParseCandidate { SourceId = StableId.Candidate(capture.Id, 1, 0), Content = "AI 内容" },
        });

        SetHumanRevised(db, initial.Id);

        var outcome = new SqliteMemoApplyStore(db.Factory).Apply(result.Id, Now.AddMinutes(1));

        Assert.Equal(MemoApplyOutcome.HumanRevised, outcome);
        Assert.Equal("原文", db.Items.GetById(initial.Id)!.Content);
        Assert.Equal("Skipped", dbFactoryParseStatus(db, result.Id));
    }

    [Fact]
    public void Apply_StaleOperationVersionIsSkipped()
    {
        using var db = new TestDatabase();
        var (capture, _) = Capture(db, "原文");
        var result = AddResult(db, capture, new[]
        {
            new MemoParseCandidate { SourceId = StableId.Candidate(capture.Id, 1, 0), Content = "旧结果" },
        });

        db.Captures.IncrementParseOperationVersion(capture.Id);

        var outcome = new SqliteMemoApplyStore(db.Factory).Apply(result.Id, Now.AddMinutes(1));

        Assert.Equal(MemoApplyOutcome.VersionConflict, outcome);
        Assert.Equal("Skipped", dbFactoryParseStatus(db, result.Id));
    }

    [Fact]
    public void Apply_DeletedMemoIsNotResurrected()
    {
        using var db = new TestDatabase();
        var (capture, initial) = Capture(db, "删除中的原文");
        var result = AddResult(db, capture, new[]
        {
            new MemoParseCandidate { SourceId = StableId.Candidate(capture.Id, 1, 0), Content = "晚到的 AI 内容" },
        });

        Assert.True(db.Items.TrySetStatus(initial.Id, ItemStatus.Deleted, Now));

        var outcome = new SqliteMemoApplyStore(db.Factory).Apply(result.Id, Now.AddMinutes(1));

        Assert.Equal(MemoApplyOutcome.ItemNotOpen, outcome);
        Assert.Equal(ItemStatus.Deleted, db.Items.GetById(initial.Id)!.Status);
        Assert.Equal("删除中的原文", db.Items.GetById(initial.Id)!.Content);
        Assert.Equal("Skipped", dbFactoryParseStatus(db, result.Id));
    }

    [Fact]
    public void Apply_CreatedRawMigration_DoesNotCreateHistoricalReminder()
    {
        using var db = new TestDatabase();
        var old = Now.AddDays(-5);
        var capture = new Capture(
            Guid.NewGuid(),
            "2026-10-20T11:00:00+08:00 旧提醒",
            old,
            "Asia/Shanghai",
            CaptureParseStatus.Pending,
            null,
            old);
        db.Captures.Add(capture);
        new LegacyMemoMigration(db.Factory, new Clock(Now), NullLogger<LegacyMemoMigration>.Instance).Run();
        var memo = db.Queries.QueryItems("capture_id = $id", ("$id", capture.Id.ToString("N"))).Single();

        var result = AddResult(db, capture, new[]
        {
            new MemoParseCandidate
            {
                SourceId = StableId.Candidate(capture.Id, 1, 0),
                Content = "旧提醒",
                SourceText = "2026-10-20T11:00:00+08:00 旧提醒",
                RemindText = "2026-10-20T11:00:00+08:00",
                RemindAtUtc = new DateTimeOffset(2026, 10, 20, 3, 0, 0, TimeSpan.Zero),
            },
        });

        var outcome = new SqliteMemoApplyStore(db.Factory).Apply(result.Id, Now);

        Assert.Equal(MemoApplyOutcome.Applied, outcome);
        Assert.Null(db.Reminders.GetActiveForItem(memo.Id));
    }

    [Fact]
    public void Apply_CreatedRawMigration_KeepsFutureReminder()
    {
        using var db = new TestDatabase();
        var old = Now.AddDays(-5);
        var capture = new Capture(
            Guid.NewGuid(),
            "2026-10-26T15:00:00+08:00 未来提醒",
            old,
            "Asia/Shanghai",
            CaptureParseStatus.Pending,
            null,
            old);
        db.Captures.Add(capture);
        new LegacyMemoMigration(db.Factory, new Clock(Now), NullLogger<LegacyMemoMigration>.Instance).Run();
        var memo = db.Queries.QueryItems("capture_id = $id", ("$id", capture.Id.ToString("N"))).Single();

        var result = AddResult(db, capture, new[]
        {
            new MemoParseCandidate
            {
                SourceId = StableId.Candidate(capture.Id, 1, 0),
                Content = "未来提醒",
                SourceText = "2026-10-26T15:00:00+08:00 未来提醒",
                RemindText = "2026-10-26T15:00:00+08:00",
                RemindAtUtc = new DateTimeOffset(2026, 10, 26, 7, 0, 0, TimeSpan.Zero),
            },
        });

        var outcome = new SqliteMemoApplyStore(db.Factory).Apply(result.Id, Now);

        Assert.Equal(MemoApplyOutcome.Applied, outcome);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 26, 7, 0, 0, TimeSpan.Zero),
            db.Reminders.GetActiveForItem(memo.Id)!.ReminderAtUtc);
    }

    [Fact]
    public void Apply_MissingMigrationContext_DoesNotAutoCreateReminder()
    {
        using var db = new TestDatabase();
        var (capture, initial) = Capture(db, "2026-10-26T15:00:00+08:00 无来源");
        using (var connection = db.Factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM migration_context WHERE capture_id = $id;";
            command.Parameters.AddWithValue("$id", capture.Id.ToString("N"));
            command.ExecuteNonQuery();
        }

        var result = AddResult(db, capture, new[]
        {
            new MemoParseCandidate
            {
                SourceId = StableId.Candidate(capture.Id, 1, 0),
                Content = "无来源",
                SourceText = "2026-10-26T15:00:00+08:00 无来源",
                RemindText = "2026-10-26T15:00:00+08:00",
                RemindAtUtc = new DateTimeOffset(2026, 10, 26, 7, 0, 0, TimeSpan.Zero),
            },
        });

        var outcome = new SqliteMemoApplyStore(db.Factory).Apply(result.Id, Now);

        Assert.Equal(MemoApplyOutcome.Applied, outcome);
        Assert.Equal("无来源", db.Items.GetById(initial.Id)!.Content);
        Assert.Null(db.Reminders.GetActiveForItem(initial.Id));
    }

    [Fact]
    public void Apply_OldResultWithoutEvidence_KeepsContentButDropsReminder()
    {
        using var db = new TestDatabase();
        var (capture, initial) = Capture(db, "旧结果原文");
        var result = AddResult(db, capture, new[]
        {
            new MemoParseCandidate
            {
                SourceId = StableId.Candidate(capture.Id, 1, 0),
                Content = "旧结果内容",
                RemindAtUtc = Now.AddHours(2),
            },
        });

        var outcome = new SqliteMemoApplyStore(db.Factory).Apply(result.Id, Now);

        Assert.Equal(MemoApplyOutcome.Applied, outcome);
        Assert.Equal("旧结果内容", db.Items.GetById(initial.Id)!.Content);
        Assert.Null(db.Reminders.GetActiveForItem(initial.Id));
    }

    [Fact]
    public void Apply_CurrentImmediateIntent_CreatesOnePlanAtCaptureTime()
    {
        using var db = new TestDatabase();
        var (capture, initial) = Capture(db, "现在提醒我提交报表");
        var result = AddResult(db, capture, new[]
        {
            new MemoParseCandidate
            {
                SourceId = StableId.Candidate(capture.Id, 1, 0),
                Content = "提交报表",
                SourceText = "现在提醒我提交报表",
                RemindText = "现在提醒",
                RemindAtUtc = capture.CapturedAtUtc,
                RemindNow = true,
            },
        });

        var outcome = new SqliteMemoApplyStore(db.Factory).Apply(result.Id, Now);

        Assert.Equal(MemoApplyOutcome.Applied, outcome);
        var plan = db.Reminders.GetActiveForItem(initial.Id);
        Assert.NotNull(plan);
        Assert.Equal(capture.CapturedAtUtc, plan!.ReminderAtUtc);
    }

    [Fact]
    public void Apply_ReminderAlreadyPastAtCaptureIsNotCreated()
    {
        using var db = new TestDatabase();
        var (capture, initial) = Capture(db, "过去的事情");
        var result = AddResult(db, capture, new[]
        {
            new MemoParseCandidate
            {
                SourceId = StableId.Candidate(capture.Id, 1, 0),
                Content = "历史资料",
                RemindAtUtc = capture.CapturedAtUtc.AddMinutes(-1),
            },
        });

        Assert.Equal(MemoApplyOutcome.Applied, new SqliteMemoApplyStore(db.Factory).Apply(result.Id, Now));
        Assert.Null(db.Reminders.GetActiveForItem(initial.Id));
    }

    private static (Capture Capture, Item Memo) Capture(TestDatabase db, string text)
    {
        var result = new MemoCaptureService(new SqliteMemoCaptureStore(db.Factory), new Clock(Now))
            .Capture(text, "Asia/Shanghai");
        return (result.Capture, result.Memo);
    }

    private static MemoParseRecord AddResult(
        TestDatabase db,
        Capture capture,
        IReadOnlyList<MemoParseCandidate> candidates)
    {
        var result = new MemoParseRecord(
            Guid.NewGuid(),
            capture.Id,
            1,
            1,
            MemoParseStatus.AwaitingApply,
            candidates,
            "{}",
            "test",
            MemoParsePrompt.Version,
            MemoParsePrompt.SchemaVersion,
            1,
            null,
            Now,
            Now);
        new SqliteMemoParseRepository(db.Factory).Add(result);
        return result;
    }

    private static void SetHumanRevised(TestDatabase db, Guid itemId)
    {
        using var connection = db.Factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET human_revised = 1 WHERE id = $id;";
        command.Parameters.AddWithValue("$id", itemId.ToString("N"));
        command.ExecuteNonQuery();
    }

    private static string? dbFactoryParseStatus(TestDatabase db, Guid resultId)
    {
        using var connection = db.Factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM memo_parse_results WHERE id = $id;";
        command.Parameters.AddWithValue("$id", resultId.ToString("N"));
        return command.ExecuteScalar() as string;
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
