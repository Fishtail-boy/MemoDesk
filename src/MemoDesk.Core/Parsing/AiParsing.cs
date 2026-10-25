using MemoDesk.Core.Drafting;

namespace MemoDesk.Core.Parsing;

public sealed record AiParseRequest(
    string RawText,
    DateTimeOffset CapturedAtUtc,
    string TimeZoneId,
    string PromptVersion,
    string SchemaVersion);

public sealed record AiParseResponse(
    string RawJson,
    string Model,
    IReadOnlyList<DraftCandidate> Candidates,
    IReadOnlyList<string> Notes);

public enum AiFailureKind
{
    /// <summary>网络、超时、429、服务端临时错误：可有限重试。</summary>
    Transient = 0,

    /// <summary>API Key 或配置错误：提示修正，不重试。</summary>
    Configuration = 1,

    /// <summary>空响应、截断、结构校验失败：可重新解析或手动整理。</summary>
    InvalidResponse = 2,
}

public sealed class AiParseException : Exception
{
    public AiParseException(AiFailureKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public AiFailureKind Kind { get; }

    public bool IsRetryable => Kind == AiFailureKind.Transient;
}

public interface IAiParser
{
    Task<AiParseResponse> ParseAsync(AiParseRequest request, CancellationToken cancellationToken);
}
