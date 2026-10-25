using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MemoDesk.Core;
using MemoDesk.Core.Parsing;
using MemoDesk.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace MemoDesk.Infrastructure.Parsing;

/// <summary>
/// DeepSeek Chat Completions 客户端（设计 4）：JSON Output、可配置地址与模型，
/// 错误按“可重试/配置错误/无效响应”分类，交给解析协调器决定重试。
/// </summary>
public sealed class DeepSeekParser : IAiParser, IMemoAiParser
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };

    private readonly AppSettings _settings;
    private readonly ILogger<DeepSeekParser> _logger;

    public DeepSeekParser(AppSettings settings, ILogger<DeepSeekParser> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AiParseResponse> ParseAsync(AiParseRequest request, CancellationToken cancellationToken)
    {
        var (content, modelName) = await SendChatAsync(
            request,
            ParsePrompt.SystemPrompt,
            ParsePrompt.BuildUserPrompt(request),
            cancellationToken).ConfigureAwait(false);
        return AiResponseMapper.Map(content, modelName, request);
    }

    public async Task<MemoParseResponse> ParseMemoAsync(AiParseRequest request, CancellationToken cancellationToken)
    {
        var (content, modelName) = await SendChatAsync(
            request,
            MemoParsePrompt.SystemPrompt,
            MemoParsePrompt.BuildUserPrompt(request),
            cancellationToken).ConfigureAwait(false);
        return MemoResponseMapper.Map(content, modelName, request);
    }

    private async Task<(string Content, string Model)> SendChatAsync(
        AiParseRequest request,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        var apiKey = _settings.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AiParseException(AiFailureKind.Configuration, "未配置 DeepSeek API Key，请在“设置”中填写。");
        }

        var model = _settings.ApiModel;
        var baseUrl = _settings.ApiBaseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate($"{baseUrl}/chat/completions", UriKind.Absolute, out var endpoint))
        {
            throw new AiParseException(AiFailureKind.Configuration, "DeepSeek 接口地址无效，请检查设置。");
        }

        var payload = BuildPayload(model, systemPrompt, userPrompt);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new AiParseException(AiFailureKind.Transient, "请求 DeepSeek 超时。", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AiParseException(AiFailureKind.Transient, $"网络请求失败：{ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new AiParseException(AiFailureKind.Configuration, "DeepSeek API Key 无效或无权访问。");
            }

            if ((int)response.StatusCode == 429)
            {
                throw new AiParseException(AiFailureKind.Transient, "DeepSeek 请求被限流（429），稍后会自动重试。");
            }

            if ((int)response.StatusCode >= 500)
            {
                throw new AiParseException(AiFailureKind.Transient, $"DeepSeek 服务暂时不可用（{(int)response.StatusCode}）。");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AiParseException(
                    AiFailureKind.Configuration,
                    $"DeepSeek 请求失败（{(int)response.StatusCode}）：{Truncate(body, 200)}");
            }

            ChatEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<ChatEnvelope>(body, JsonDefaults.Options);
            }
            catch (JsonException ex)
            {
                throw new AiParseException(AiFailureKind.InvalidResponse, "DeepSeek 返回结构无法解析。", ex);
            }

            var choice = envelope?.Choices?.FirstOrDefault();
            var content = choice?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new AiParseException(AiFailureKind.InvalidResponse, "DeepSeek 返回了空内容。");
            }

            if (string.Equals(choice?.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
            {
                throw new AiParseException(AiFailureKind.InvalidResponse, "DeepSeek 输出被截断，请重新解析。");
            }

            var modelName = string.IsNullOrWhiteSpace(envelope?.Model) ? model : envelope.Model!;
            _logger.LogInformation("DeepSeek 解析返回：model={Model}", modelName);
            return (content, modelName);
        }
    }

    private static Dictionary<string, object?> BuildPayload(string model, string systemPrompt, string userPrompt) => new()
    {
        ["model"] = model,
        ["messages"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = systemPrompt,
            },
            new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = userPrompt,
            },
        },
        ["response_format"] = new Dictionary<string, object?> { ["type"] = "json_object" },
        ["temperature"] = 0.2,
        ["max_tokens"] = 3000,
        ["stream"] = false,
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private sealed class ChatEnvelope
    {
        public List<ChatChoice>? Choices { get; set; }

        public string? Model { get; set; }
    }

    private sealed class ChatChoice
    {
        public ChatMessage? Message { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private sealed class ChatMessage
    {
        public string? Content { get; set; }
    }
}
