using MemoDesk.Core.Capturing;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Timeline;
using MemoDesk.Infrastructure.Queries;
using MemoDesk.Infrastructure.Storage;

namespace MemoDesk.Infrastructure.Tests;

public sealed class QueryAndSearchTests
{
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    private static readonly DateOnly Today = new(2026, 10, 21);

    [Fact]
    public void DesktopMemosCanRetainPreviousUnfinishedWithoutShowingFutureOrCompleted()
    {
        using var db = new TestDatabase();
        var old = db.SeedItem(TestDatabase.Memo("昨天未完成", Today.AddDays(-1)));
        var current = db.SeedItem(TestDatabase.Memo("今日备忘", Today));
        db.SeedItem(TestDatabase.Memo("明天再显示", Today.AddDays(1)));
        db.SeedItem(TestDatabase.Memo("昨日已完成", Today.AddDays(-1)) with { Status = ItemStatus.Completed });
        var rows = db.Queries.GetWidgetMemos(Today, includePrevious: true);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, item => item.Id == old.Id);
        Assert.Contains(rows, item => item.Id == current.Id);
        Assert.Single(db.Queries.GetWidgetMemos(Today));
    }

    [Fact]
    public void TodaySnapshot_SeparatesTodayOverdueAndUnscheduled()
    {
        using var db = new TestDatabase();

        db.SeedItem(TestDatabase.Todo("今天的事", Today));
        db.SeedItem(TestDatabase.Todo("昨天逾期", Today.AddDays(-1)));
        db.SeedItem(TestDatabase.Todo("没日期"));
        db.SeedItem(TestDatabase.Todo("今天截止", dueAt: new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.FromHours(8))));
        db.SeedItem(TestDatabase.Memo("今天的备忘", Today));
        db.SeedItem(TestDatabase.Memo("昨天的备忘", Today.AddDays(-1)));

        var snapshot = db.Queries.GetTodaySnapshot(Today, TestDatabase.Now, Shanghai);

        Assert.Equal(2, snapshot.TodayTodos.Count); // 今天的事 + 今天截止
        Assert.Single(snapshot.OverdueTodos);
        Assert.Equal(1, snapshot.UnscheduledCount);
        Assert.Equal(1, snapshot.UnprocessedMemoCount);
    }

    [Fact]
    public void TodaySnapshot_ReminderTodayCountsAsTodayTodo()
    {
        using var db = new TestDatabase();
        var item = TestDatabase.Todo("靠提醒进入今天");
        var reminderAt = new DateTimeOffset(2026, 10, 21, 11, 0, 0, TimeSpan.FromHours(8));
        db.SeedItem(item, TestDatabase.Reminder(item.Id, reminderAt));

        var snapshot = db.Queries.GetTodaySnapshot(Today, TestDatabase.Now, Shanghai);

        Assert.Contains(snapshot.TodayTodos, todo => todo.Id == item.Id);
    }

    [Fact]
    public void WorkRecords_ShowCaptureAndCompletionFromSameMemo()
    {
        using var db = new TestDatabase();
        var capture = new MemoCaptureService(
                new SqliteMemoCaptureStore(db.Factory),
                new Clock(TestDatabase.Now))
            .Capture("今天要提交报表", "Asia/Shanghai");
        var items = new MemoDesk.Core.Items.ItemService(db.Items, new Clock(TestDatabase.Now));
        Assert.True(items.Complete(capture.Memo.Id));

        var records = db.Queries.GetWorkRecords(Today, Today, Shanghai);

        Assert.Equal(2, records.Count);
        Assert.Contains(records, entry => entry.Kind == WorkRecordKind.Captured && entry.Content == "今天要提交报表");
        Assert.Contains(records, entry => entry.Kind == WorkRecordKind.Completed && entry.Note == "标记完成");
        Assert.All(records, entry => Assert.Equal(capture.Memo.Id, entry.Item.Id));
    }

    [Fact]
    public void WorkRecords_SearchMatchesRawTextAndDeletedMemoIsHidden()
    {
        using var db = new TestDatabase();
        var capture = new MemoCaptureService(
                new SqliteMemoCaptureStore(db.Factory),
                new Clock(TestDatabase.Now))
            .Capture("只在原文里出现的检索词", "Asia/Shanghai");

        var hits = db.Queries.GetWorkRecords(Today, Today, Shanghai, "检索词");
        Assert.Single(hits);

        new MemoDesk.Core.Items.ItemService(db.Items, new Clock(TestDatabase.Now)).Delete(capture.Memo.Id);
        Assert.Empty(db.Queries.GetWorkRecords(Today, Today, Shanghai));
    }

    [Fact]
    public void UnifiedMemos_IncludeLegacyTypesWithoutChangingTheirIds()
    {
        using var db = new TestDatabase();
        var todo = db.SeedItem(TestDatabase.Todo("旧待办", Today));
        var memo = db.SeedItem(TestDatabase.Memo("旧备忘", Today));
        var work = db.SeedItem(TestDatabase.WorkLog("旧工作记录", Today, new TimeOnly(9, 0), null, WorkTimePrecision.Exact));

        var rows = db.Queries.GetUnifiedMemos(MemoFilter.Unprocessed);

        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, item => item.Id == todo.Id);
        Assert.Contains(rows, item => item.Id == memo.Id);
        Assert.Contains(rows, item => item.Id == work.Id);
    }

    [Fact]
    public void UnifiedMemos_SearchMatchesOriginalTextAndEscapesLike()
    {
        using var db = new TestDatabase();
        var capture = new Capture(
            Guid.NewGuid(),
            "raw-only-keyword 100%_done",
            TestDatabase.Now,
            "Asia/Shanghai",
            CaptureParseStatus.Pending,
            null,
            TestDatabase.Now);
        db.Captures.Add(capture);
        var item = TestDatabase.Memo("rewritten-summary", Today);
        db.SeedItem(item, capture: capture);

        var rawHit = db.Queries.GetUnifiedMemos(MemoFilter.Unprocessed, "RAW-ONLY-KEYWORD");
        Assert.Single(rawHit);
        Assert.Equal(item.Id, rawHit[0].Id);

        var escaped = db.Queries.GetUnifiedMemos(MemoFilter.Unprocessed, "100%_done");
        Assert.Single(escaped);
        Assert.Equal(item.Id, escaped[0].Id);
    }

    [Fact]
    public void WorkLogs_CrossDayRangeOverlappingFilterIsIncluded()
    {
        using var db = new TestDatabase();
        db.SeedItem(TestDatabase.WorkLog("跨日值守", Today.AddDays(-1), new TimeOnly(22, 0), Today, WorkTimePrecision.Range));
        db.SeedItem(TestDatabase.WorkLog("普通记录", Today, new TimeOnly(9, 0), null, WorkTimePrecision.Exact));

        var logs = db.Queries.GetWorkLogs(Today, Today);

        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public void WorkLogs_OutsideRange_Excluded()
    {
        using var db = new TestDatabase();
        db.SeedItem(TestDatabase.WorkLog("很久以前", Today.AddDays(-10), new TimeOnly(9, 0), null, WorkTimePrecision.Exact));

        Assert.Empty(db.Queries.GetWorkLogs(Today.AddDays(-1), Today));
    }

    [Fact]
    public void WidgetMemos_OnlyTodayUnprocessed()
    {
        using var db = new TestDatabase();
        db.SeedItem(TestDatabase.Memo("今天未处理", Today));
        db.SeedItem(TestDatabase.Memo("今天已完成", Today, ItemStatus.Completed));
        db.SeedItem(TestDatabase.Memo("昨天", Today.AddDays(-1)));

        var memos = db.Queries.GetWidgetMemos(Today);

        var memo = Assert.Single(memos);
        Assert.Equal("今天未处理", memo.Content);
    }

    [Fact]
    public void Timeline_IncludesWorkAndCompletionEventsInOrder()
    {
        using var db = new TestDatabase();
        var work = TestDatabase.WorkLog("早上的工作", Today, new TimeOnly(9, 15), null, WorkTimePrecision.Exact);
        db.SeedItem(work);

        var todo = TestDatabase.Todo("完成的事", Today);
        var seeded = db.SeedItem(todo);
        db.Items.TrySetStatus(seeded.Id, ItemStatus.Completed, new DateTimeOffset(2026, 10, 21, 3, 40, 0, TimeSpan.Zero));

        var timeline = db.Queries.GetTimeline(Today, Shanghai);

        Assert.Equal(2, timeline.Count);
        Assert.Equal(TimelineEventType.WorkOccurred, timeline[0].EventType);
        Assert.Equal(TimelineEventType.ItemCompleted, timeline[1].EventType);
        Assert.Equal("早上的工作", timeline[0].Content);
        Assert.Equal("09:15", timeline[0].DisplayTime);
    }

    [Fact]
    public void Timeline_FollowsEditedWorkDate_AndExcludesDeleted()
    {
        using var db = new TestDatabase();
        var workTime = new WorkTimeInfo(Today, new TimeOnly(9, 0), null, null, WorkTimePrecision.Exact);
        var item = db.SeedItem(TestDatabase.WorkLog("可编辑的记录", Today, new TimeOnly(9, 0)));

        db.Items.Update(item with
        {
            WorkTime = workTime with { Date = Today.AddDays(-1) },
            UpdatedAtUtc = TestDatabase.Now.AddMinutes(1),
        });

        Assert.DoesNotContain(db.Queries.GetTimeline(Today, Shanghai), entry => entry.ItemId == item.Id);
        Assert.Contains(db.Queries.GetTimeline(Today.AddDays(-1), Shanghai), entry => entry.ItemId == item.Id);

        db.Items.TrySetStatus(item.Id, ItemStatus.Deleted, TestDatabase.Now.AddMinutes(2));
        Assert.DoesNotContain(db.Queries.GetTimeline(Today.AddDays(-1), Shanghai), entry => entry.ItemId == item.Id);
    }

    [Fact]
    public void Timeline_CrossDayRangeAppearsOnOverlappingDaysWithRangeLabel()
    {
        using var db = new TestDatabase();
        var item = new Item
        {
            Id = Guid.NewGuid(),
            Type = ItemType.WorkLog,
            Content = "三天值守",
            Status = ItemStatus.Open,
            WorkTime = new WorkTimeInfo(
                Today.AddDays(-1),
                new TimeOnly(22, 0),
                new TimeOnly(2, 0),
                "multi-day",
                WorkTimePrecision.Range,
                Today.AddDays(1)),
            CreatedAtUtc = TestDatabase.Now,
            UpdatedAtUtc = TestDatabase.Now,
        };
        db.SeedItem(item);

        foreach (var day in new[] { Today.AddDays(-1), Today, Today.AddDays(1) })
        {
            var entries = db.Queries.GetTimeline(day, Shanghai)
                .Where(entry => entry.ItemId == item.Id)
                .ToArray();
            var entry = Assert.Single(entries);
            Assert.Contains("跨日", entry.DisplayTime);
            Assert.Contains("22:00", entry.DisplayTime);
            Assert.DoesNotContain("00:00", entry.DisplayTime);
        }
    }

    [Fact]
    public void Search_MatchesChineseTags_EvenWhenJsonEscaped()
    {
        using var db = new TestDatabase();
        db.SeedItem(TestDatabase.Memo(
            "标签测试事项",
            Today,
            tags: new[] { "搜索标签", "regression" }));

        Assert.Single(db.Search.Search(new SearchQuery("搜索标签")));
        Assert.Single(db.Search.Search(new SearchQuery("regression")));
    }

    [Fact]
    public void Search_MatchesContentRawTextAndTags()
    {
        using var db = new TestDatabase();
        var capture = db.SeedCapture("今天处理了登录故障，客户要求保留日志");
        db.SeedItem(TestDatabase.Memo("联系客户确认日志保留期限", Today, tags: new[] { "客户", "日志" }), capture: capture);

        var byContent = db.Search.Search(new SearchQuery("确认"));
        Assert.Single(byContent);

        var byRaw = db.Search.Search(new SearchQuery("客户要求"));
        Assert.NotEmpty(byRaw);

        var byTag = db.Search.Search(new SearchQuery("日志"));
        Assert.NotEmpty(byTag);
    }

    [Fact]
    public void Search_WildcardsAreEscaped()
    {
        using var db = new TestDatabase();
        db.SeedItem(TestDatabase.Memo("普通内容", Today));
        db.SeedItem(TestDatabase.Memo("含百分之%的内容", Today));

        Assert.Single(db.Search.Search(new SearchQuery("%")));
        Assert.Empty(db.Search.Search(new SearchQuery("_")));
    }

    [Fact]
    public void Search_FiltersByTypeAndDate()
    {
        using var db = new TestDatabase();
        db.SeedItem(TestDatabase.Todo("整理接口文档", Today));
        db.SeedItem(TestDatabase.Memo("接口文档备忘", Today));

        var todos = db.Search.Search(new SearchQuery("接口", Type: ItemType.Todo));
        Assert.Single(todos);
        Assert.Equal(ItemType.Todo, todos[0].Item.Type);

        var yesterday = db.Search.Search(new SearchQuery("接口", From: Today.AddDays(-2), To: Today.AddDays(-1)));
        Assert.Empty(yesterday);
    }

    [Fact]
    public void Search_ExcludesDeletedAndCompletedByStatusFilter()
    {
        using var db = new TestDatabase();
        var completed = TestDatabase.Todo("已完成的事项", Today, status: ItemStatus.Completed);
        db.SeedItem(completed);

        Assert.Empty(db.Search.Search(new SearchQuery("已完成", Status: SearchStatusFilter.Open)));
        Assert.Single(db.Search.Search(new SearchQuery("已完成", Status: SearchStatusFilter.Completed)));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
