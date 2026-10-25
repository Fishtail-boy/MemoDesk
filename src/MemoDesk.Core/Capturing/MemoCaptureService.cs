using MemoDesk.Core.Domain;
using MemoDesk.Core.Time;
using MemoDesk.Core.Timeline;
using MemoDesk.Core.Parsing;

namespace MemoDesk.Core.Capturing;

/// <summary>
/// 回车即保存：原文、正式备忘和录入事件一次提交，不等待 AI。
/// 同一输入框的重复提交/失败重试可复用调用方给出的稳定 ID。
/// </summary>
public sealed class MemoCaptureService
{
    private readonly IMemoCaptureStore _store;
    private readonly TimeProvider _clock;

    public MemoCaptureService(IMemoCaptureStore store, TimeProvider clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public MemoCaptureResult Capture(
        string rawText,
        string timeZoneId,
        Guid? captureId = null,
        Guid? memoId = null,
        DateTimeOffset? capturedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            throw new ArgumentException("原文不能为空。", nameof(rawText));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        var timeZone = BusinessTime.Resolve(timeZoneId);

        var now = capturedAtUtc ?? _clock.GetUtcNow();
        var reminder = LocalReminderResolver.Resolve(rawText, now, timeZone);
        var capture = new Capture(
            Id: captureId ?? Guid.NewGuid(),
            RawText: rawText,
            CapturedAtUtc: now,
            CapturedTimeZoneId: timeZone.Id,
            ParseStatus: CaptureParseStatus.Pending,
            LastError: null,
            RecordedAtUtc: now,
            ParseOperationVersion: 1);

        var content = reminder?.Content ?? rawText.Trim();
        var memo = new Item
        {
            Id = memoId ?? Guid.NewGuid(),
            CaptureId = capture.Id,
            CandidateSourceId = capture.Id,
            Type = ItemType.Memo,
            Content = content,
            Status = ItemStatus.Open,
            MemoDate = BusinessTime.DateOf(now, timeZone),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        var capturedEvent = new TimelineEvent(
            Guid.NewGuid(),
            memo.Id,
            TimelineEventType.MemoCaptured,
            capture.CapturedAtUtc,
            now,
            content);

        return _store.Save(capture, memo, capturedEvent, reminder);
    }
}
