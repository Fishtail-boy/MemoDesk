namespace MemoDesk.Core.Capturing;

/// <summary>
/// 一条原始输入（设计 7.1）。原文不可被整理后内容覆盖；
/// 捕获时刻以 UTC 保存，同时保存捕获时区，供相对时间语义使用（设计 3.2）。
/// </summary>
public sealed record Capture(
    Guid Id,
    string RawText,
    DateTimeOffset CapturedAtUtc,
    string CapturedTimeZoneId,
    CaptureParseStatus ParseStatus,
    string? LastError,
    DateTimeOffset RecordedAtUtc,
    int ParseOperationVersion = 1);
