using System.Threading.Channels;
using MemoDesk.Core.Capturing;
using MemoDesk.Core.Time;
using Microsoft.Extensions.Logging;

namespace MemoDesk.Core.Parsing;

/// <summary>
/// “先保存、后整理”的后台链路：原文已对应一条正式备忘；AI 结果先落盘，
/// 再通过 CAS 事务补充同一条备忘（必要时拆分）；失败只影响整理，不影响备忘可用。
/// </summary>
public sealed class MemoParseCoordinator
{
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(6),
    };

    private readonly IMemoAiParser _parser;
    private readonly ICaptureRepository _captures;
    private readonly IMemoParseRepository _results;
    private readonly IMemoApplyStore _applyStore;
    private readonly TimeProvider _clock;
    private readonly ILogger<MemoParseCoordinator> _logger;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();

    /// <summary>整理结果保存/应用/失败后触发，供界面刷新。</summary>
    public event Action<Guid>? MemoChanged;

    public MemoParseCoordinator(
        IMemoAiParser parser,
        ICaptureRepository captures,
        IMemoParseRepository results,
        IMemoApplyStore applyStore,
        TimeProvider clock,
        ILogger<MemoParseCoordinator> logger)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _captures = captures ?? throw new ArgumentNullException(nameof(captures));
        _results = results ?? throw new ArgumentNullException(nameof(results));
        _applyStore = applyStore ?? throw new ArgumentNullException(nameof(applyStore));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Notify(Guid captureId) => _queue.Writer.TryWrite(captureId);

    /// <summary>人工重试：推进原文操作版本，旧结果晚到不能应用。</summary>
    public void Retry(Guid captureId)
    {
        var version = _captures.IncrementParseOperationVersion(captureId);
        if (version == 0)
        {
            return;
        }

        _results.MarkSuperseded(captureId, version, "manual-retry", _clock.GetUtcNow());
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

    public async Task ProcessAsync(Guid captureId, CancellationToken cancellationToken)
    {
        var capture = _captures.GetById(captureId);
        if (capture is null)
        {
            return;
        }

        var latest = _results.GetLatestForCapture(captureId);
        if (latest is { Status: MemoParseStatus.AwaitingApply })
        {
            var applyOutcome = Apply(capture, latest);
            if (applyOutcome != MemoApplyOutcome.VersionConflict)
            {
                return;
            }

            capture = _captures.GetById(captureId);
            if (capture is null || capture.ParseStatus != CaptureParseStatus.Pending)
            {
                return;
            }
        }

        if (capture.ParseStatus != CaptureParseStatus.Pending)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        var operationVersion = capture.ParseOperationVersion;

        // 新一次解析开始：旧的同操作版本结果不再等待应用。
        _results.MarkSuperseded(captureId, operationVersion, "new-parse", now);

        var record = new MemoParseRecord(
            Guid.NewGuid(),
            captureId,
            operationVersion,
            (latest?.Version ?? 0) + 1,
            MemoParseStatus.Parsing,
            Array.Empty<MemoParseCandidate>(),
            null,
            null,
            MemoParsePrompt.Version,
            MemoParsePrompt.SchemaVersion,
            0,
            null,
            now,
            now);

        _results.Add(record);
        _captures.UpdateParseStatus(captureId, CaptureParseStatus.Parsing, null);

        var request = new AiParseRequest(
            capture.RawText,
            capture.CapturedAtUtc,
            capture.CapturedTimeZoneId,
            MemoParsePrompt.Version,
            MemoParsePrompt.SchemaVersion);

        var maxAttempts = RetryDelays.Length + 1;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var response = await _parser.ParseMemoAsync(request, cancellationToken).ConfigureAwait(false);
                var candidates = MemoCandidateNormalizer.Normalize(
                    response.Candidates,
                    captureId,
                    operationVersion);

                _results.SaveResult(
                    record.Id,
                    MemoParseStatus.AwaitingApply,
                    candidates,
                    response.RawJson,
                    response.Model,
                    MemoParsePrompt.Version,
                    MemoParsePrompt.SchemaVersion,
                    attempt,
                    null,
                    _clock.GetUtcNow());

                Apply(capture, record with
                {
                    Status = MemoParseStatus.AwaitingApply,
                    Candidates = candidates,
                });
                return;
            }
            catch (AiParseException ex) when (ex.IsRetryable && attempt < maxAttempts)
            {
                _logger.LogWarning(ex, "备忘整理临时失败，将重试（第 {Attempt} 次）", attempt);
                await Task.Delay(RetryDelays[attempt - 1], cancellationToken).ConfigureAwait(false);
            }
            catch (AiParseException ex)
            {
                Fail(capture, record, attempt, ex.Message);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "备忘整理出现未预期错误");
                Fail(capture, record, attempt, $"整理出现未预期错误：{ex.Message}");
                return;
            }
        }
    }

    private MemoApplyOutcome Apply(Capture capture, MemoParseRecord result)
    {
        var now = _clock.GetUtcNow();
        MemoApplyOutcome outcome;
        try
        {
            outcome = _applyStore.Apply(result.Id, now);
        }
        catch (Exception ex)
        {
            // 结果已经落盘，保留 AwaitingApply，下次启动先应用而不是重新调用 AI。
            _logger.LogError(ex, "应用备忘整理结果失败：{ParseResultId}", result.Id);
            MemoChanged?.Invoke(capture.Id);
            return MemoApplyOutcome.NotFound;
        }

        if (outcome == MemoApplyOutcome.Applied)
        {
            MemoChanged?.Invoke(capture.Id);
            return outcome;
        }

        if (outcome == MemoApplyOutcome.VersionConflict)
        {
            // 人工重试已推进操作版本，新的解析会处理当前状态。
            MemoChanged?.Invoke(capture.Id);
            return outcome;
        }

        var current = _captures.GetById(capture.Id);
        if (current is not null
            && current.ParseOperationVersion == result.OperationVersion
            && current.ParseStatus != CaptureParseStatus.Pending)
        {
            _captures.UpdateParseStatus(capture.Id, CaptureParseStatus.Parsed, null);
        }

        MemoChanged?.Invoke(capture.Id);
        return outcome;
    }

    private void Fail(Capture capture, MemoParseRecord record, int attempt, string message)
    {
        var now = _clock.GetUtcNow();

        // 旧请求晚到：只保留失败结果，不改新操作版本的状态。
        var current = _captures.GetById(capture.Id);
        if (current is not null && current.ParseOperationVersion == record.OperationVersion)
        {
            _results.SaveResult(
                record.Id,
                MemoParseStatus.Failed,
                Array.Empty<MemoParseCandidate>(),
                null,
                null,
                MemoParsePrompt.Version,
                MemoParsePrompt.SchemaVersion,
                attempt,
                message,
                now);
            _captures.UpdateParseStatus(capture.Id, CaptureParseStatus.Failed, message);
        }

        _logger.LogWarning("备忘整理失败：{Message}", message);
        MemoChanged?.Invoke(capture.Id);
    }

    private void Recover()
    {
        var seen = new HashSet<Guid>();

        // 上次进程在调用 AI 时崩溃：原文回到待解析；已有结果会在 ProcessAsync 中先应用。
        foreach (var capture in LoadAllCaptures(CaptureParseStatus.Parsing))
        {
            _captures.UpdateParseStatus(capture.Id, CaptureParseStatus.Pending, null);
        }

        foreach (var result in LoadAllResults(MemoParseStatus.AwaitingApply))
        {
            if (seen.Add(result.CaptureId))
            {
                Notify(result.CaptureId);
            }
        }

        foreach (var capture in LoadAllCaptures(CaptureParseStatus.Pending))
        {
            if (seen.Add(capture.Id))
            {
                Notify(capture.Id);
            }
        }
    }

    private IReadOnlyList<Capture> LoadAllCaptures(CaptureParseStatus status)
    {
        var result = new List<Capture>();
        var afterAt = DateTimeOffset.MinValue;
        var afterId = Guid.Empty;
        while (true)
        {
            var batch = _captures.GetByStatusAfter(status, afterAt, afterId, 250);
            if (batch.Count == 0)
            {
                break;
            }

            result.AddRange(batch);
            var last = batch[^1];
            afterAt = last.RecordedAtUtc;
            afterId = last.Id;
            if (batch.Count < 250)
            {
                break;
            }
        }

        return result;
    }

    private IReadOnlyList<MemoParseRecord> LoadAllResults(MemoParseStatus status)
    {
        var result = new List<MemoParseRecord>();
        var afterAt = DateTimeOffset.MinValue;
        var afterId = Guid.Empty;
        while (true)
        {
            var batch = _results.GetByStatusAfter(status, afterAt, afterId, 250);
            if (batch.Count == 0)
            {
                break;
            }

            result.AddRange(batch);
            var last = batch[^1];
            afterAt = last.CreatedAtUtc;
            afterId = last.Id;
            if (batch.Count < 250)
            {
                break;
            }
        }

        return result;
    }
}
