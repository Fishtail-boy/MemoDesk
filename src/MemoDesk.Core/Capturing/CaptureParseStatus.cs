namespace MemoDesk.Core.Capturing;

/// <summary>
/// 原文的解析状态（设计 7.1）。M1 只产生 Pending；解析链路在 M2 接入。
/// </summary>
public enum CaptureParseStatus
{
    /// <summary>已留存原文，等待解析。</summary>
    Pending = 0,

    /// <summary>解析进行中。</summary>
    Parsing = 1,

    /// <summary>解析成功，已产生待确认草稿。</summary>
    Parsed = 2,

    /// <summary>解析失败，可重试或手动整理。</summary>
    Failed = 3,

    /// <summary>用户丢弃了该原文对应的草稿，不再处理。</summary>
    Discarded = 4,
}
