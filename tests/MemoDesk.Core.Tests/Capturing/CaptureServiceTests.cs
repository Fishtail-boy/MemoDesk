using MemoDesk.Core.Capturing;

namespace MemoDesk.Core.Tests.Capturing;

public sealed class CaptureServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 6, 30, 0, TimeSpan.Zero);

    private static (CaptureService Service, InMemoryCaptureRepository Repository) CreateService()
    {
        var repository = new InMemoryCaptureRepository();
        var service = new CaptureService(repository, new FixedTimeProvider(Now));
        return (service, repository);
    }

    [Fact]
    public void CaptureText_PreservesRawTextExactly()
    {
        var (service, _) = CreateService();
        const string rawText = "  第一行\n第二行 <保留符号> & \"引号\"   ";

        var capture = service.CaptureText(rawText, "Asia/Shanghai");

        Assert.Equal(rawText, capture.RawText);
    }

    [Fact]
    public void CaptureText_CreatesPendingCaptureWithUtcTimeAndTimeZone()
    {
        var (service, _) = CreateService();

        var capture = service.CaptureText("记录一条", "Asia/Shanghai");

        Assert.Equal(CaptureParseStatus.Pending, capture.ParseStatus);
        Assert.Equal(Now, capture.CapturedAtUtc);
        Assert.Equal(Now, capture.RecordedAtUtc);
        Assert.Equal("Asia/Shanghai", capture.CapturedTimeZoneId);
        Assert.Null(capture.LastError);
        Assert.NotEqual(Guid.Empty, capture.Id);
    }

    [Fact]
    public void CaptureText_PersistsCaptureToRepository()
    {
        var (service, repository) = CreateService();

        var capture = service.CaptureText("落盘", "Asia/Shanghai");

        var stored = Assert.Single(repository.Items);
        Assert.Equal(capture, stored);
    }

    [Fact]
    public void CaptureText_EachCallGetsUniqueId()
    {
        var (service, _) = CreateService();

        var first = service.CaptureText("一", "Asia/Shanghai");
        var second = service.CaptureText("二", "Asia/Shanghai");

        Assert.NotEqual(first.Id, second.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void CaptureText_RejectsBlankText(string rawText)
    {
        var (service, repository) = CreateService();

        Assert.Throws<ArgumentException>(() => service.CaptureText(rawText, "Asia/Shanghai"));
        Assert.Empty(repository.Items);
    }

    [Fact]
    public void CaptureText_RejectsUnknownTimeZone()
    {
        var (service, repository) = CreateService();

        Assert.Throws<TimeZoneNotFoundException>(() => service.CaptureText("内容", "Not/AZone"));
        Assert.Empty(repository.Items);
    }

    [Fact]
    public void CaptureText_RejectsEmptyTimeZoneId()
    {
        var (service, _) = CreateService();

        Assert.Throws<ArgumentException>(() => service.CaptureText("内容", " "));
    }
}
