using MemoDesk.Core.Reminders;

namespace MemoDesk.Core.Tests.Reminders;

public sealed class ReminderDeadlineTrackerTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc = new(2026,10,25,2,0,0,TimeSpan.Zero);
        public long Stamp;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => Stamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    [Fact]
    public void WallClockChangesDoNotRestartRunningShortCountdown()
    {
        var clock = new Clock();
        var plan = new ReminderPlan(Guid.NewGuid(),Guid.NewGuid(),clock.Utc.AddSeconds(3),true,1,clock.Utc,clock.Utc,
            Source:"LocalExplicit",RelativeSeconds:3,CapturedAtUtc:clock.Utc);
        var tracker = new ReminderDeadlineTracker();
        Assert.Equal(TimeSpan.FromSeconds(3),tracker.Remaining(plan,clock));
        clock.Utc=clock.Utc.AddHours(-1);clock.Stamp=TimeSpan.TicksPerSecond*3;
        Assert.Equal(TimeSpan.Zero,tracker.Remaining(plan,clock));
        Assert.Equal(TimeSpan.FromSeconds(4),tracker.Remaining(plan with {PlanVersion=2,ReminderAtUtc=clock.Utc.AddSeconds(4)},clock));
    }
}
