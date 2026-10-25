using MemoDesk.Core;
using MemoDesk.Core.Drafting;
using MemoDesk.Core.Domain;

namespace MemoDesk.Infrastructure.Tests;

public sealed class DraftEditPersistenceTests
{
    [Fact]
    public void SaveEditState_UsesOptimisticVersion_AndRoundTripsRawInput()
    {
        using var db = new TestDatabase();
        var capture = db.SeedCapture();
        var draft = new ParseDraft(
            Guid.NewGuid(), capture.Id, 1, DraftStatus.AwaitingConfirmation,
            new[] { new DraftCandidate { Type = ItemType.Todo, Content = "原始" } },
            null, "m", "p1", "v1", 0, null, TestDatabase.Now, TestDatabase.Now);
        db.Drafts.Add(draft);

        var state = new DraftEditState
        {
            Candidates = new List<DraftCandidateEditState>
            {
                new()
                {
                    SourceId = draft.Candidates[0].SourceId,
                    Type = ItemType.Todo,
                    Content = "用户编辑",
                    RemindDate = new DateOnly(2026, 10, 23),
                    RemindTime = "99:99",
                    DirtyFields = new List<string> { "Content", "RemindTime" },
                },
            },
        };

        Assert.True(db.Drafts.SaveEditState(draft.Id, 0, JsonDefaults.Serialize(state), TestDatabase.Now.AddSeconds(1)));

        // 旧版本号不能再次覆盖。
        Assert.False(db.Drafts.SaveEditState(draft.Id, 0, JsonDefaults.Serialize(state), TestDatabase.Now.AddSeconds(2)));

        var reloaded = db.Drafts.GetLatestForCapture(capture.Id)!;
        Assert.Equal(1, reloaded.EditVersion);
        var loadedState = JsonDefaults.Deserialize<DraftEditState>(reloaded.EditStateJson!)!;
        var candidate = Assert.Single(loadedState.Candidates);
        Assert.Equal("用户编辑", candidate.Content);
        Assert.Equal("99:99", candidate.RemindTime);
        Assert.Equal(new DateOnly(2026, 10, 23), candidate.RemindDate);
    }

    [Fact]
    public void SaveEditState_AfterConfirm_IsRejected()
    {
        using var db = new TestDatabase();
        var capture = db.SeedCapture();
        var draft = new ParseDraft(
            Guid.NewGuid(), capture.Id, 1, DraftStatus.AwaitingConfirmation,
            Array.Empty<DraftCandidate>(), null, "m", "p1", "v1", 0, null, TestDatabase.Now, TestDatabase.Now);
        db.Drafts.Add(draft);
        db.Drafts.UpdateStatus(draft.Id, DraftStatus.Confirmed, null, TestDatabase.Now);

        Assert.False(db.Drafts.SaveEditState(draft.Id, 0, "{}", TestDatabase.Now));
    }
}
