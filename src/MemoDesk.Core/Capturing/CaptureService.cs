namespace MemoDesk.Core.Capturing;

/// <summary>
/// 捕获服务：校验并生成待解析的原文记录。不依赖 UI 与具体存储（设计 6.2）。
/// </summary>
public sealed class CaptureService
{
    private readonly ICaptureRepository _repository;
    private readonly TimeProvider _clock;

    public CaptureService(ICaptureRepository repository, TimeProvider clock)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// 保存一条原文。原文按用户输入原样保存（不 Trim、不改写）；
    /// 捕获时间取当前 UTC 时刻，业务时区由调用方给出并在此校验。
    /// </summary>
    public Capture CaptureText(string rawText, string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            throw new ArgumentException("原文不能为空。", nameof(rawText));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        // 无法解释的时区标识不应进入数据库：相对时间语义依赖它。
        _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        var now = _clock.GetUtcNow();
        var capture = new Capture(
            Id: Guid.NewGuid(),
            RawText: rawText,
            CapturedAtUtc: now,
            CapturedTimeZoneId: timeZoneId,
            ParseStatus: CaptureParseStatus.Pending,
            LastError: null,
            RecordedAtUtc: now);

        _repository.Add(capture);
        return capture;
    }
}
