using MemoDesk.Core.Capturing;
using MemoDesk.Core.Drafting;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Parsing;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemoDesk.Core.Tests.Parsing;

public sealed class ParseCoordinatorTests
{
    private static (ParseCoordinator Coordinator, InMemoryCaptureRepository Captures, InMemoryDraftRepository Drafts, ScriptedAiParser Parser) Create()
    {
        var captures = new InMemoryCaptureRepository();
        var drafts = new InMemoryDraftRepository();
        var parser = new ScriptedAiParser();
        var coordinator = new ParseCoordinator(
            parser, captures, drafts, new FixedTimeProvider(TestData.CapturedAt), NullLogger<ParseCoordinator>.Instance);
        return (coordinator, captures, drafts, parser);
    }

    [Fact]
    public async Task ProcessAsync_Success_CreatesAwaitingDraft()
    {
        var (coordinator, captures, drafts, parser) = Create();
        var capture = TestData.Capture();
        captures.Add(capture);
        parser.EnqueueSuccess(new DraftCandidate
        {
            Type = ItemType.Todo,
            Content = "复核登录故障",
            BusinessDate = new DateOnly(2026, 10, 22),
        });

        await coordinator.ProcessAsync(capture.Id, CancellationToken.None);

        var draft = Assert.Single(drafts.Drafts);
        Assert.Equal(DraftStatus.AwaitingConfirmation, draft.Status);
        Assert.Single(draft.Candidates);
        Assert.Equal(CaptureParseStatus.Parsed, captures.GetById(capture.Id)!.ParseStatus);
    }

    [Fact]
    public async Task ProcessAsync_ConfigurationFailure_MarksDraftAndCaptureFailed()
    {
        var (coordinator, captures, drafts, parser) = Create();
        var capture = TestData.Capture();
        captures.Add(capture);
        parser.EnqueueFailure(new AiParseException(AiFailureKind.Configuration, "未配置 API Key"));

        await coordinator.ProcessAsync(capture.Id, CancellationToken.None);

        var draft = Assert.Single(drafts.Drafts);
        Assert.Equal(DraftStatus.Failed, draft.Status);
        Assert.Contains("API Key", draft.LastError);
        Assert.Equal(CaptureParseStatus.Failed, captures.GetById(capture.Id)!.ParseStatus);
        Assert.Equal(1, parser.CallCount);
    }

    [Fact]
    public async Task ProcessAsync_TransientFailure_RetriesBeforeSucceeding()
    {
        var (coordinator, captures, drafts, parser) = Create();
        var capture = TestData.Capture();
        captures.Add(capture);
        parser.EnqueueFailure(new AiParseException(AiFailureKind.Transient, "超时"));
        parser.EnqueueSuccess(new DraftCandidate { Type = ItemType.Memo, Content = "备忘" });

        await coordinator.ProcessAsync(capture.Id, CancellationToken.None);

        Assert.Equal(2, parser.CallCount);
        Assert.Equal(DraftStatus.AwaitingConfirmation, Assert.Single(drafts.Drafts).Status);
    }

    [Fact]
    public async Task Retry_SupersedesFailedDraftAndCreatesNewVersion()
    {
        var (coordinator, captures, drafts, parser) = Create();
        var capture = TestData.Capture();
        captures.Add(capture);
        parser.EnqueueFailure(new AiParseException(AiFailureKind.InvalidResponse, "结构无效"));

        await coordinator.ProcessAsync(capture.Id, CancellationToken.None);
        Assert.Equal(DraftStatus.Failed, Assert.Single(drafts.Drafts).Status);

        coordinator.Retry(capture.Id);
        parser.EnqueueSuccess(new DraftCandidate { Type = ItemType.Memo, Content = "第二次解析" });
        await coordinator.ProcessAsync(capture.Id, CancellationToken.None);

        Assert.Equal(2, drafts.Drafts.Count);
        Assert.Equal(DraftStatus.Superseded, drafts.Drafts[0].Status);
        var latest = drafts.GetLatestForCapture(capture.Id)!;
        Assert.Equal(2, latest.Version);
        Assert.Equal(DraftStatus.AwaitingConfirmation, latest.Status);
    }

    [Fact]
    public void CreateManualDraft_ProducesEditableAwaitingDraft()
    {
        var (coordinator, captures, drafts, _) = Create();
        var capture = TestData.Capture("手动整理内容");
        captures.Add(capture);

        var draft = coordinator.CreateManualDraft(capture.Id);

        Assert.Equal(DraftStatus.AwaitingConfirmation, draft.Status);
        var candidate = Assert.Single(draft.Candidates);
        Assert.Equal("手动整理内容", candidate.Content);
        Assert.Equal(new DateOnly(2026, 10, 21), candidate.BusinessDate);
        Assert.NotEmpty(candidate.Issues);
        Assert.Equal(CaptureParseStatus.Parsed, captures.GetById(capture.Id)!.ParseStatus);
    }
}
