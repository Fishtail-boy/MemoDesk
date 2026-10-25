using System.Threading.Channels;
using MemoDesk.Core.Capturing;
using MemoDesk.Core.Drafting;
using MemoDesk.Core.Parsing;
using MemoDesk.Core.Time;
using Microsoft.Extensions.Logging;

namespace MemoDesk.Core.Parsing;

/// <summary>
/// 后台解析协调器（设计 4）：原文先落盘、解析任务可恢复、结果进入待确认草稿。
/// 启动时把上次异常退出的“解析中”恢复为待解析，并处理所有待解析原文。
/// </summary>
public sealed class ParseCoordinator
{
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(6),
    };

    private readonly IAiParser _parser;
    private readonly ICaptureRepository _captures;
    private readonly IParseDraftRepository _drafts;
    private readonly TimeProvider _clock;
    private readonly ILogger<ParseCoordinator> _logger;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();

    /// <summary>解析完成（成功或失败）或手动草稿创建后触发，供界面刷新。</summary>
    public event Action<Guid>? DraftChanged;

    public ParseCoordinator(
        IAiParser parser,
        ICaptureRepository captures,
        IParseDraftRepository drafts,
        TimeProvider clock,
        ILogger<ParseCoordinator> logger)
    {
        _parser = parser;
        _captures = captures;
        _drafts = drafts;
        _clock = clock;
        _logger = logger;
    }

    public void Notify(Guid captureId) => _queue.Writer.TryWrite(captureId);

    /// <summary>人工触发重新解析（解析失败后的“重试”）。</summary>
    public void Retry(Guid captureId)
    {
        _captures.UpdateParseStatus(captureId, CaptureParseStatus.Pending, null);
        Notify(captureId);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Recover();
        try
        {
            await foreach (var captureId in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                await ProcessAsync(captureId, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出。
        }
    }

    private void Recover()
    {
        foreach (var capture in _captures.GetByStatus(CaptureParseStatus.Parsing, 1000))
        {
            _captures.UpdateParseStatus(capture.Id, CaptureParseStatus.Pending, null);
        }

        foreach (var capture in _captures.GetByStatus(CaptureParseStatus.Pending, 1000))
        {
            Notify(capture.Id);
        }
    }

    public async Task ProcessAsync(Guid captureId, CancellationToken cancellationToken)
    {
        var capture = _captures.GetById(captureId);
        if (capture is null || capture.ParseStatus != CaptureParseStatus.Pending)
        {
            return;
        }

        var previous = _drafts.GetLatestForCapture(captureId);
        if (previous is { Status: DraftStatus.AwaitingConfirmation or DraftStatus.Failed or DraftStatus.Parsing })
        {
            _drafts.UpdateStatus(previous.Id, DraftStatus.Superseded, previous.LastError, _clock.GetUtcNow());
        }

        var now = _clock.GetUtcNow();
        var draft = new ParseDraft(
            Id: Guid.NewGuid(),
            CaptureId: captureId,
            Version: (previous?.Version ?? 0) + 1,
            Status: DraftStatus.Parsing,
            Candidates: Array.Empty<DraftCandidate>(),
            RawAiJson: null,
            Model: null,
            PromptVersion: ParsePrompt.Version,
            SchemaVersion: ParsePrompt.SchemaVersion,
            AttemptCount: 0,
            LastError: null,
            CreatedAtUtc: now,
            UpdatedAtUtc: now);

        _drafts.Add(draft);
        _captures.UpdateParseStatus(captureId, CaptureParseStatus.Parsing, null);

        var request = new AiParseRequest(
            capture.RawText,
            capture.CapturedAtUtc,
            capture.CapturedTimeZoneId,
            ParsePrompt.Version,
            ParsePrompt.SchemaVersion);

        var maxAttempts = RetryDelays.Length + 1;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var response = await _parser.ParseAsync(request, cancellationToken).ConfigureAwait(false);
                var completedAt = _clock.GetUtcNow();

                _drafts.SaveParsingResult(
                    draft.Id,
                    DraftStatus.AwaitingConfirmation,
                    response.Candidates,
                    response.RawJson,
                    response.Model,
                    ParsePrompt.Version,
                    ParsePrompt.SchemaVersion,
                    attempt,
                    null,
                    completedAt);

                _captures.UpdateParseStatus(captureId, CaptureParseStatus.Parsed, null);
                DraftChanged?.Invoke(captureId);
                return;
            }
            catch (AiParseException ex) when (ex.IsRetryable && attempt < maxAttempts)
            {
                _logger.LogWarning(ex, "解析临时失败，将重试（第 {Attempt} 次）", attempt);
                await Task.Delay(RetryDelays[attempt - 1], cancellationToken).ConfigureAwait(false);
            }
            catch (AiParseException ex)
            {
                Fail(captureId, draft, attempt, ex.Message);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解析出现未预期错误");
                Fail(captureId, draft, attempt, $"解析出现未预期错误：{ex.Message}");
                return;
            }
        }
    }

    private void Fail(Guid captureId, ParseDraft draft, int attempt, string message)
    {
        var now = _clock.GetUtcNow();
        _drafts.SaveParsingResult(
            draft.Id,
            DraftStatus.Failed,
            Array.Empty<DraftCandidate>(),
            null,
            null,
            ParsePrompt.Version,
            ParsePrompt.SchemaVersion,
            attempt,
            message,
            now);
        _captures.UpdateParseStatus(captureId, CaptureParseStatus.Failed, message);
        _logger.LogWarning("解析失败：{Message}", message);
        DraftChanged?.Invoke(captureId);
    }

    /// <summary>
    /// 解析不可用时的手动整理：创建一条只有一个待办候选的待确认草稿，
    /// 原文内容原样保留，由用户修改后确认（设计 2.2 的“手动整理”）。
    /// </summary>
    public ParseDraft CreateManualDraft(Guid captureId)
    {
        var capture = _captures.GetById(captureId)
            ?? throw new InvalidOperationException("原文不存在。");

        var previous = _drafts.GetLatestForCapture(captureId);
        if (previous is { Status: DraftStatus.AwaitingConfirmation })
        {
            return previous;
        }

        if (previous is not null)
        {
            _drafts.UpdateStatus(previous.Id, DraftStatus.Superseded, previous.LastError, _clock.GetUtcNow());
        }

        var timeZone = BusinessTime.Resolve(capture.CapturedTimeZoneId);
        var captureDate = BusinessTime.DateOf(capture.CapturedAtUtc, timeZone);
        var now = _clock.GetUtcNow();
        var candidate = new DraftCandidate
        {
            Type = Domain.ItemType.Todo,
            Content = capture.RawText.Trim(),
            BusinessDate = captureDate,
        };
        candidate.Issues.Add("AI 解析不可用，本事项由手动整理创建，请核对内容与时间。");

        var draft = new ParseDraft(
            Guid.NewGuid(),
            captureId,
            (previous?.Version ?? 0) + 1,
            DraftStatus.AwaitingConfirmation,
            new[] { candidate },
            RawAiJson: null,
            Model: null,
            PromptVersion: "manual",
            SchemaVersion: ParsePrompt.SchemaVersion,
            AttemptCount: 0,
            LastError: null,
            CreatedAtUtc: now,
            UpdatedAtUtc: now);

        _drafts.Add(draft);
        _captures.UpdateParseStatus(captureId, CaptureParseStatus.Parsed, null);
        DraftChanged?.Invoke(captureId);
        return draft;
    }
}
