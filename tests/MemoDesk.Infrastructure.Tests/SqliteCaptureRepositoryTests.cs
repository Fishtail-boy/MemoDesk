using MemoDesk.Core.Capturing;
using MemoDesk.Infrastructure.Storage;
using MemoDesk.Infrastructure.Storage.Migrations;

namespace MemoDesk.Infrastructure.Tests;

public sealed class SqliteCaptureRepositoryTests
{
    [Fact]
    public void AddThenGetById_RoundTripsAllFields()
    {
        using var db = new TempDatabase();
        new MigrationRunner(db.Factory).Migrate();
        var repository = new SqliteCaptureRepository(db.Factory);

        var capture = new Capture(
            Id: Guid.NewGuid(),
            RawText: "第一行\n第二行 <符号> & \"引号\" ",
            CapturedAtUtc: new DateTimeOffset(2026, 10, 21, 6, 30, 0, 123, TimeSpan.Zero),
            CapturedTimeZoneId: "Asia/Shanghai",
            ParseStatus: CaptureParseStatus.Pending,
            LastError: null,
            RecordedAtUtc: new DateTimeOffset(2026, 10, 21, 6, 30, 0, 456, TimeSpan.Zero));

        repository.Add(capture);

        var loaded = repository.GetById(capture.Id);
        Assert.NotNull(loaded);
        Assert.Equal(capture.Id, loaded.Id);
        Assert.Equal(capture.RawText, loaded.RawText);
        Assert.Equal(capture.CapturedAtUtc, loaded.CapturedAtUtc);
        Assert.Equal(capture.CapturedTimeZoneId, loaded.CapturedTimeZoneId);
        Assert.Equal(capture.ParseStatus, loaded.ParseStatus);
        Assert.Null(loaded.LastError);
        Assert.Equal(capture.RecordedAtUtc, loaded.RecordedAtUtc);
    }

    [Fact]
    public void GetById_Missing_ReturnsNull()
    {
        using var db = new TempDatabase();
        new MigrationRunner(db.Factory).Migrate();
        var repository = new SqliteCaptureRepository(db.Factory);

        Assert.Null(repository.GetById(Guid.NewGuid()));
    }

    [Fact]
    public void Add_IsPersistentAcrossConnections()
    {
        using var db = new TempDatabase();
        new MigrationRunner(db.Factory).Migrate();
        var repository = new SqliteCaptureRepository(db.Factory);

        var capture = new Capture(
            Guid.NewGuid(), "跨连接读取", DateTimeOffset.UtcNow,
            "Asia/Shanghai", CaptureParseStatus.Pending, null, DateTimeOffset.UtcNow);
        repository.Add(capture);

        // 新的连接工厂（模拟应用重启）仍能读到同一条记录。
        var reopened = new SqliteCaptureRepository(new SqliteConnectionFactory(db.DatabasePath));
        Assert.Equal(capture.RawText, reopened.GetById(capture.Id)?.RawText);
    }
}
