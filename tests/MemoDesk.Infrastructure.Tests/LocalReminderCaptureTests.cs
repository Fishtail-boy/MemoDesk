using MemoDesk.Core.Capturing;
using MemoDesk.Core.Parsing;
using MemoDesk.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using MemoDesk.Core.Reminders;

namespace MemoDesk.Infrastructure.Tests;

public sealed class LocalReminderCaptureTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    [Fact]
    public void CaptureImmediatelyPersistsSecondsPlanWithoutAi()
    {
        using var db = new TestDatabase();
        var result = new MemoCaptureService(new SqliteMemoCaptureStore(db.Factory), new Clock()).Capture("3s后提醒我尿尿", "Asia/Shanghai");
        var plan = db.Reminders.GetActiveForItem(result.Memo.Id)!;
        Assert.Equal(Now.AddSeconds(3), plan.ReminderAtUtc);
        Assert.Equal("LocalExplicit", plan.Source);
        Assert.Equal(3, plan.RelativeSeconds);
        Assert.Equal("尿尿", db.Items.GetById(result.Memo.Id)!.Content);
        Assert.Equal("3s后提醒我尿尿", db.Captures.GetById(result.Capture.Id)!.RawText);
        Assert.Single(db.Reminders.GetWakeablePlans());
    }
    [Fact]
    public void RetryingSameSubmissionDoesNotRestartCountdown()
    {
        using var db = new TestDatabase();
        var service = new MemoCaptureService(new SqliteMemoCaptureStore(db.Factory), new Clock());
        var first = service.Capture("3秒后提醒我喝水", "Asia/Shanghai");
        var plan = db.Reminders.GetActiveForItem(first.Memo.Id);
        var second = service.Capture("3秒后提醒我喝水", "Asia/Shanghai", first.Capture.Id, first.Memo.Id, Now.AddSeconds(20));
        Assert.True(second.AlreadyExisted);
        Assert.Equal(plan, db.Reminders.GetActiveForItem(first.Memo.Id));
    }
    [Fact]
    public void PlanFailureRollsBackCaptureMemoAndEvent()
    {
        using var db = new TestDatabase();
        using (var connection = db.Factory.Create())
        using (var command = connection.CreateCommand())
        { command.CommandText = "CREATE TRIGGER fail_local BEFORE INSERT ON reminders BEGIN SELECT RAISE(ABORT, 'plan failure'); END;"; command.ExecuteNonQuery(); }
        Assert.Throws<SqliteException>(() => new MemoCaptureService(new SqliteMemoCaptureStore(db.Factory), new Clock()).Capture("3s后提醒我尿尿", "Asia/Shanghai"));
        Assert.Empty(db.Captures.GetByStatus(CaptureParseStatus.Pending, 10));
        Assert.Empty(db.Queries.QueryItems("1=1"));
    }
    [Fact]
    public void LateAiCannotDuplicateOrRetargetLocalPlan()
    {
        using var db = new TestDatabase();
        var captured = new MemoCaptureService(new SqliteMemoCaptureStore(db.Factory), new Clock()).Capture("3s后提醒我尿尿", "Asia/Shanghai");
        var plan = db.Reminders.GetActiveForItem(captured.Memo.Id);
        var record = new MemoParseRecord(Guid.NewGuid(), captured.Capture.Id, 1, 1, MemoParseStatus.AwaitingApply,
            new[] { new MemoParseCandidate { Content="去卫生间", SourceText="3s后提醒我尿尿", RemindText="3s后", RemindAtUtc=Now.AddHours(1) },
                new MemoParseCandidate { Content="重复候选", SourceText="3s后提醒我尿尿", RemindText="3s后", RemindAtUtc=Now.AddSeconds(3) } },
            null, null, null, null, 1, null, Now, Now);
        new SqliteMemoParseRepository(db.Factory).Add(record);
        new SqliteMemoApplyStore(db.Factory).Apply(record.Id, Now.AddSeconds(40));
        Assert.Equal(plan, db.Reminders.GetActiveForItem(captured.Memo.Id));
        Assert.Single(db.Queries.QueryItems("1=1"));
    }

    [Fact]
    public void ElapsedCountdownCanBeClaimedAfterWallClockMovesBackwardsOnlyOnce()
    {
        using var db = new TestDatabase();
        var captured = new MemoCaptureService(new SqliteMemoCaptureStore(db.Factory), new Clock()).Capture("3s后提醒我尿尿", "Asia/Shanghai");
        var plan = db.Reminders.GetActiveForItem(captured.Memo.Id)!;
        var scheduler = new ReminderScheduler(db.Reminders);
        var backwardsNow = Now.AddHours(-1);
        Assert.Empty(scheduler.CollectCandidates(backwardsNow, Array.Empty<SnoozeOption>()));
        var candidate = Assert.Single(scheduler.CollectCandidates(backwardsNow, Array.Empty<SnoozeOption>(), new[] { plan }));
        Assert.True(candidate.ElapsedDeadline);
        Assert.Equal(Now.AddSeconds(3), candidate.Occurrence.ScheduledAtUtc);
        var rescanned = Assert.Single(scheduler.CollectCandidates(backwardsNow, Array.Empty<SnoozeOption>(), new[] { plan }));
        Assert.Equal(candidate.Occurrence.Id, rescanned.Occurrence.Id);
        Assert.True(scheduler.TryClaim(rescanned, "elapsed", backwardsNow));
        Assert.False(scheduler.TryClaim(candidate, "duplicate", backwardsNow));
        Assert.True(scheduler.MarkHandedOff(candidate.Occurrence.Id, "elapsed", "test", backwardsNow));
        Assert.Empty(db.Reminders.GetWakeablePlans());
    }

    [Fact]
    public void StaleElapsedSnapshotCannotFireEditedReminder()
    {
        using var db = new TestDatabase();
        var captured = new MemoCaptureService(new SqliteMemoCaptureStore(db.Factory), new Clock()).Capture("3s后提醒我尿尿", "Asia/Shanghai");
        var plan = db.Reminders.GetActiveForItem(captured.Memo.Id)!;
        var scheduler = new ReminderScheduler(db.Reminders);
        var candidate = Assert.Single(scheduler.CollectCandidates(Now, Array.Empty<SnoozeOption>(), new[] { plan }));
        db.Reminders.SetReminder(captured.Memo.Id, Now.AddHours(1), Now);
        Assert.False(scheduler.TryClaim(candidate, "stale", Now));
        Assert.Empty(scheduler.CollectCandidates(Now, Array.Empty<SnoozeOption>(), new[] { plan }));
    }

    [Fact]
    public void WallClockMovingForwardCannotBypassRunningRelativeCountdown()
    {
        using var db = new TestDatabase();
        var captured = new MemoCaptureService(new SqliteMemoCaptureStore(db.Factory), new Clock()).Capture("3s后提醒我尿尿", "Asia/Shanghai");
        var plan = db.Reminders.GetActiveForItem(captured.Memo.Id)!;
        var scheduler = new ReminderScheduler(db.Reminders);
        var forwardNow = Now.AddHours(1);
        Assert.Empty(scheduler.CollectCandidates(forwardNow, Array.Empty<SnoozeOption>(), requireElapsedForShortRelative: true));
        var candidate = Assert.Single(scheduler.CollectCandidates(forwardNow, Array.Empty<SnoozeOption>(), new[] { plan }, requireElapsedForShortRelative: true));
        Assert.True(scheduler.TryClaim(candidate, "elapsed", forwardNow));
    }
}
