using MemoDesk.Core.Drafting;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Core.Timeline;

namespace MemoDesk.Core.Tests.Drafting;

public sealed class DraftConfirmationServiceTests
{
    private static DraftConfirmationService CreateService(RecordingItemStore store) =>
        new(store, new FixedTimeProvider(TestData.CapturedAt));

    [Fact]
    public void Confirm_BuildsItemsRemindersAndTimeline()
    {
        var store = new RecordingItemStore();
        var service = CreateService(store);
        var capture = TestData.Capture();
        var draft = TestData.Draft(
            capture.Id,
            new DraftCandidate
            {
                Type = ItemType.Todo,
                Content = "复核登录故障",
                BusinessDate = new DateOnly(2026, 10, 22),
                RemindAtUtc = new DateTimeOffset(2026, 10, 22, 7, 0, 0, TimeSpan.Zero),
            },
            new DraftCandidate
            {
                Type = ItemType.WorkLog,
                Content = "处理登录故障",
                WorkTime = new WorkTimeInfo(
                    new DateOnly(2026, 10, 21),
                    new TimeOnly(9, 10),
                    null,
                    null,
                    WorkTimePrecision.Exact),
            },
            new DraftCandidate
            {
                Type = ItemType.Memo,
                Content = "客户要求保留原始日志",
                BusinessDate = new DateOnly(2026, 10, 21),
            });

        var result = service.Confirm(draft, capture, draft.Candidates);

        Assert.Equal(DraftConfirmOutcome.Confirmed, result.Outcome);
        Assert.Equal(3, store.Items.Count);
        Assert.Single(store.Reminders);
        Assert.Equal(store.Items[0].Id, store.Reminders[0].ItemId);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 7, 0, 0, TimeSpan.Zero), store.Reminders[0].ReminderAtUtc);

        // 工作记录时间线由事项本身派生，确认时不再写独立事件。
        Assert.Empty(store.Events);

        Assert.All(store.Items, item =>
        {
            Assert.Equal(draft.CaptureId, item.CaptureId);
            Assert.Equal(ItemStatus.Open, item.Status);
        });
    }

    [Fact]
    public void Confirm_RejectsPastReminder()
    {
        var store = new RecordingItemStore();
        var service = CreateService(store);
        var capture = TestData.Capture();
        var draft = TestData.Draft(capture.Id, new DraftCandidate
        {
            Type = ItemType.Todo,
            Content = "过期提醒",
            RemindAtUtc = TestData.CapturedAt.AddHours(-1),
        });

        var result = service.Confirm(draft, capture, draft.Candidates);

        Assert.Equal(DraftConfirmOutcome.Invalid, result.Outcome);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Confirm_RejectsEmptyCandidates()
    {
        var store = new RecordingItemStore();
        var service = CreateService(store);
        var capture = TestData.Capture();
        var draft = TestData.Draft(capture.Id);

        var result = service.Confirm(draft, capture, Array.Empty<DraftCandidate>());

        Assert.Equal(DraftConfirmOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void Confirm_RejectsBlankContent()
    {
        var store = new RecordingItemStore();
        var service = CreateService(store);
        var capture = TestData.Capture();
        var draft = TestData.Draft(capture.Id, new DraftCandidate { Type = ItemType.Memo, Content = "   " });

        var result = service.Confirm(draft, capture, draft.Candidates);

        Assert.Equal(DraftConfirmOutcome.Invalid, result.Outcome);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Confirm_NormalizesTags()
    {
        var store = new RecordingItemStore();
        var service = CreateService(store);
        var capture = TestData.Capture();
        var draft = TestData.Draft(capture.Id, new DraftCandidate
        {
            Type = ItemType.Memo,
            Content = "标签",
            BusinessDate = new DateOnly(2026, 10, 21),
            Tags = new List<string>
            {
                " 客户 ", "客户", "", new string('长', 40), "A", "a",
                "1", "2", "3", "4", "5", "6", "7", "8",
            },
        });

        var result = service.Confirm(draft, capture, draft.Candidates);

        Assert.Equal(DraftConfirmOutcome.Confirmed, result.Outcome);
        var tags = store.Items.Single().Tags;
        Assert.Equal("客户", tags[0]);
        Assert.Equal(10, tags.Count);
        Assert.Equal(30, tags[1].Length);
        Assert.Single(tags, t => t.Equals("A", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Confirm_DraftNotAwaiting_ReturnsInvalid()
    {
        var store = new RecordingItemStore();
        var service = CreateService(store);
        var capture = TestData.Capture();
        var draft = TestData.Draft(capture.Id, new DraftCandidate { Type = ItemType.Memo, Content = "x" })
            with { Status = DraftStatus.Failed };

        var result = service.Confirm(draft, capture, draft.Candidates);

        Assert.Equal(DraftConfirmOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void Confirm_AlreadyConfirmedDraft_ReturnsAlreadyConfirmed()
    {
        var store = new RecordingItemStore();
        var service = CreateService(store);
        var capture = TestData.Capture();
        var draft = TestData.Draft(capture.Id, new DraftCandidate { Type = ItemType.Memo, Content = "x" })
            with { Status = DraftStatus.Confirmed };

        var result = service.Confirm(draft, capture, draft.Candidates);

        Assert.Equal(DraftConfirmOutcome.AlreadyConfirmed, result.Outcome);
    }
}
