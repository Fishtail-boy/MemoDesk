using MemoDesk.Core.Capturing;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Timeline;
using MemoDesk.Core.Parsing;

namespace MemoDesk.Core.Capturing;

/// <summary>一次“原文 + 正式备忘 + 录入事件”的原子保存结果。</summary>
public sealed record MemoCaptureResult(Capture Capture, Item Memo, bool AlreadyExisted,
    LocalReminderResolution? LocalReminder = null);

/// <summary>即时捕获的存储入口；实现必须在同一个事务中写入全部记录。</summary>
public interface IMemoCaptureStore
{
    MemoCaptureResult Save(Capture capture, Item memo, TimelineEvent capturedEvent,
        LocalReminderResolution? reminder = null);
}
