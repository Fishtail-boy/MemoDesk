using MemoDesk.Core;
using MemoDesk.Core.Drafting;
using MemoDesk.Core.Domain;

namespace MemoDesk.Core.Tests.Drafting;

public sealed class DraftCandidateEditorTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    private static readonly DateOnly Day = new(2026, 10, 22);

    private static DraftCandidateEditState State(DraftCandidate original) => new()
    {
        SourceId = original.SourceId,
        Type = original.Type,
        Content = original.Content,
        Date = original.Type == ItemType.WorkLog ? original.WorkTime?.Date : original.BusinessDate,
        WorkStart = original.WorkTime?.Start?.ToString("HH:mm") ?? string.Empty,
        WorkEnd = original.WorkTime?.End?.ToString("HH:mm") ?? string.Empty,
        WorkEndDate = original.WorkTime?.EndDate,
        TagsText = string.Join(", ", original.Tags),
        Priority = original.Priority,
        IssuesText = string.Join("\n", original.Issues),
    };

    [Fact]
    public void UnchangedPeriod_PreservesPrecisionAndOriginalText()
    {
        var original = new DraftCandidate
        {
            Type = ItemType.WorkLog,
            Content = "上午写文档",
            WorkTime = new WorkTimeInfo(Day, null, null, "上午", WorkTimePrecision.Period),
        };

        var result = DraftCandidateEditor.Apply(original, State(original), Zone, Day);

        Assert.Equal(WorkTimePrecision.Period, result.WorkTime!.Precision);
        Assert.Equal("上午", result.WorkTime.OriginalText);
        Assert.Empty(result.ValidationErrors);
    }

    [Fact]
    public void UnchangedRange_PreservesExplicitEndDate()
    {
        var original = new DraftCandidate
        {
            Type = ItemType.WorkLog,
            Content = "三天值守",
            WorkTime = new WorkTimeInfo(
                Day, new TimeOnly(9, 0), new TimeOnly(17, 0), "multi-day", WorkTimePrecision.Range, Day.AddDays(2)),
        };

        var result = DraftCandidateEditor.Apply(original, State(original), Zone, Day);

        Assert.Equal(WorkTimePrecision.Range, result.WorkTime!.Precision);
        Assert.Equal(Day.AddDays(2), result.WorkTime.EndDate);
    }

    [Fact]
    public void Priority_RoundTrips()
    {
        var original = new DraftCandidate
        {
            Type = ItemType.Todo,
            Content = "优先级",
            Priority = ItemPriority.High,
            BusinessDate = Day,
        };

        var result = DraftCandidateEditor.Apply(original, State(original), Zone, Day);

        Assert.Equal(ItemPriority.High, result.Priority);
    }

    [Fact]
    public void InvalidReminderTime_IsValidationError_AndNoReminder()
    {
        var original = new DraftCandidate { Type = ItemType.Todo, Content = "非法时间" };
        var state = State(original);
        state.RemindDate = Day.AddDays(1);
        state.RemindTime = "99:99";

        var result = DraftCandidateEditor.Apply(original, state, Zone, Day);

        Assert.Null(result.RemindAtUtc);
        Assert.NotEmpty(result.ValidationErrors);
        Assert.Contains(result.ValidationErrors, error => error.Contains("提醒时间格式无效"));
    }

    [Fact]
    public void ExactToOvernightRange_InfersNextDayEnd()
    {
        var original = new DraftCandidate
        {
            Type = ItemType.WorkLog,
            Content = "夜间值守",
            WorkTime = new WorkTimeInfo(Day, new TimeOnly(9, 0), null, null, WorkTimePrecision.Exact),
        };
        var state = State(original);
        state.WorkStart = "22:00";
        state.WorkEnd = "02:00";

        var result = DraftCandidateEditor.Apply(original, state, Zone, Day);

        Assert.Equal(WorkTimePrecision.Range, result.WorkTime!.Precision);
        Assert.Equal(Day.AddDays(1), result.WorkTime.EndDate);
        Assert.Empty(result.ValidationErrors);
    }

    [Fact]
    public void ExplicitEndDate_IsPreferredOverInference()
    {
        var original = new DraftCandidate
        {
            Type = ItemType.WorkLog,
            Content = "跨日",
            WorkTime = new WorkTimeInfo(Day, new TimeOnly(9, 0), new TimeOnly(17, 0), null, WorkTimePrecision.Range),
        };
        var state = State(original);
        state.WorkEndDate = Day.AddDays(2);

        var result = DraftCandidateEditor.Apply(original, state, Zone, Day);

        Assert.Equal(WorkTimePrecision.Range, result.WorkTime!.Precision);
        Assert.Equal(Day.AddDays(2), result.WorkTime.EndDate);
    }

    [Fact]
    public void DateOnlyChange_OnPeriod_PreservesTextAndShiftsEndDate()
    {
        var original = new DraftCandidate
        {
            Type = ItemType.WorkLog,
            Content = "上午",
            WorkTime = new WorkTimeInfo(
                Day, null, null, "上午", WorkTimePrecision.Period, Day.AddDays(2)),
        };
        var state = State(original);
        state.Date = Day.AddDays(-1);

        var result = DraftCandidateEditor.Apply(original, state, Zone, Day);

        Assert.Equal(WorkTimePrecision.Period, result.WorkTime!.Precision);
        Assert.Equal("上午", result.WorkTime.OriginalText);
        Assert.Equal(Day.AddDays(-1), result.WorkTime.Date);
        Assert.Equal(Day.AddDays(1), result.WorkTime.EndDate);
    }

    [Fact]
    public void BackwardsRange_IsValidationError()
    {
        var original = new DraftCandidate
        {
            Type = ItemType.WorkLog,
            Content = "倒置区间",
            WorkTime = new WorkTimeInfo(Day, new TimeOnly(9, 0), new TimeOnly(10, 0), null, WorkTimePrecision.Range),
        };
        var state = State(original);
        state.WorkEndDate = Day.AddDays(-1);

        var result = DraftCandidateEditor.Apply(original, state, Zone, Day);

        Assert.NotEmpty(result.ValidationErrors);
        Assert.Contains(result.ValidationErrors, error => error.Contains("结束时间"));
    }

    [Fact]
    public void Confirmation_RejectsBackwardsWorkRange()
    {
        var capture = TestData.Capture();
        var candidate = new DraftCandidate
        {
            Type = ItemType.WorkLog,
            Content = "倒置区间",
            WorkTime = new WorkTimeInfo(
                Day, new TimeOnly(9, 0), new TimeOnly(10, 0), null, WorkTimePrecision.Range, Day.AddDays(-1)),
        };
        var draft = TestData.Draft(capture.Id, candidate);
        var store = new RecordingItemStore();

        var result = new DraftConfirmationService(store, new FixedTimeProvider(TestData.CapturedAt))
            .Confirm(draft, capture, new[] { candidate });

        Assert.Equal(DraftConfirmOutcome.Invalid, result.Outcome);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Confirmation_RejectsCandidateWithValidationErrors()
    {
        var capture = TestData.Capture();
        var bad = new DraftCandidate { Type = ItemType.Todo, Content = "非法时间" };
        bad.ValidationErrors.Add("提醒时间格式无效（应为 HH:mm），请修改后再确认。");
        var draft = TestData.Draft(capture.Id, bad);
        var store = new RecordingItemStore();

        var result = new DraftConfirmationService(store, new FixedTimeProvider(TestData.CapturedAt))
            .Confirm(draft, capture, new[] { bad });

        Assert.Equal(DraftConfirmOutcome.Invalid, result.Outcome);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void EditState_SerializesRawInput()
    {
        var state = new DraftEditState
        {
            Candidates = new List<DraftCandidateEditState>
            {
                new()
                {
                    SourceId = Guid.NewGuid(),
                    Type = ItemType.Todo,
                    Content = "原始输入",
                    RemindDate = Day,
                    RemindTime = "99:99",
                    DirtyFields = new List<string> { "RemindTime" },
                },
            },
        };

        var json = JsonDefaults.Serialize(state);
        var loaded = JsonDefaults.Deserialize<DraftEditState>(json)!;

        Assert.Equal("99:99", loaded.Candidates.Single().RemindTime);
        Assert.Equal(Day, loaded.Candidates.Single().RemindDate);
        Assert.Contains("RemindTime", loaded.Candidates.Single().DirtyFields);
    }
}
