using System.Net;
using System.Text;
using MemoDesk.Core.Parsing;
using MemoDesk.Infrastructure.Parsing;
using MemoDesk.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemoDesk.Infrastructure.Tests;

public sealed class DeepSeekParserTests
{
    private static readonly AiParseRequest Request = new("合成测试备忘", TestDatabase.Now, "Asia/Shanghai", MemoParsePrompt.Version, MemoParsePrompt.SchemaVersion);

    [Theory]
    [InlineData("https://api.example.test/v1", true)]
    [InlineData("http://localhost:8080/v1", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("http://example.test", false)]
    [InlineData("https://user:secret@example.test", false)]
    [InlineData("https://example.test?api_key=secret", false)]
    [InlineData("https://example.test/#fragment", false)]
    [InlineData("file:///tmp/service", false)]
    public void EndpointPolicyProtectsCredentials(string url, bool valid) => Assert.Equal(valid, ApiEndpoint.TryCreate(url, out _));

    [Theory]
    [InlineData(400, AiFailureKind.Configuration)]
    [InlineData(401, AiFailureKind.Configuration)]
    [InlineData(403, AiFailureKind.Configuration)]
    [InlineData(302, AiFailureKind.Configuration)]
    [InlineData(429, AiFailureKind.Transient)]
    [InlineData(503, AiFailureKind.Transient)]
    public async Task ErrorResponsesAreClassifiedWithoutReadingOrExposingBody(int code, AiFailureKind kind)
    {
        using var db = new TestDatabase();
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage((HttpStatusCode)code)
            { Content = new StreamContent(new StalledStream()) }));
        var error = await Assert.ThrowsAsync<AiParseException>(() => Parser(db, client).ParseMemoAsync(Request, default));
        Assert.Equal(kind, error.Kind);
        Assert.DoesNotContain("合成测试备忘", error.ToString());
    }

    [Fact]
    public async Task BodyReadHasDeadlineAndIsRetryable()
    {
        using var db = new TestDatabase();
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new StalledStream()) }));
        var pending = Parser(db, client, TimeSpan.FromMilliseconds(50)).ParseMemoAsync(Request, default);
        var error = await Assert.ThrowsAsync<AiParseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(AiFailureKind.Transient, error.Kind);
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        using var db = new TestDatabase();
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new StalledStream()) }));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Parser(db, client).ParseMemoAsync(Request, cancel.Token));
    }

    [Fact]
    public async Task ChunkedResponseIsBoundedEvenWithoutContentLength()
    {
        using var db = new TestDatabase();
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new UnboundedStream()) }));
        var error = await Assert.ThrowsAsync<AiParseException>(() => Parser(db, client).ParseMemoAsync(Request, default));
        Assert.Equal(AiFailureKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task ValidResponseUsesConfiguredEndpointAndBearerHeader()
    {
        using var db = new TestDatabase();
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("https://example.test/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-key", request.Headers.Authorization.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"choices":[{"message":{"content":"{\"items\":[{\"content\":\"合成测试备忘\",\"source_text\":\"合成测试备忘\",\"tags\":[]}]}"},"finish_reason":"stop"}]}
                """, Encoding.UTF8, "application/json") };
        }));
        var result = await Parser(db, client).ParseMemoAsync(Request, default);
        Assert.NotNull(result);
    }

    private static DeepSeekParser Parser(TestDatabase db, HttpClient client, TimeSpan? timeout = null)
    {
        var settings = new AppSettings(db.SettingsStore, new Base64Protector())
            { ApiKey = "synthetic-key", ApiBaseUrl = "https://example.test/v1" };
        return new(settings, NullLogger<DeepSeekParser>.Instance, client, timeout);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private class UnboundedStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { buffer.Span.Fill((byte)'x'); return ValueTask.FromResult(buffer.Length); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StalledStream : UnboundedStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
    }
}
