using MemoDesk.Core.Capturing;
using MemoDesk.Core.Domain;
using MemoDesk.Infrastructure.Storage;
using MemoDesk.Infrastructure.Storage.Migrations;

namespace MemoDesk.Infrastructure.Tests;

public sealed class SqliteDigitalAssetStoreTests
{
    [Fact]
    public void UpgradeAndManageAssets_PreservesExistingDataAndPersistsChanges()
    {
        using var db = new TempDatabase();
        new MigrationRunner(db.Factory, MigrationsCatalog.All.Where(m => m.Version <= 11).ToArray()).Migrate();
        var captures = new SqliteCaptureRepository(db.Factory);
        var oldCapture = new Capture(Guid.NewGuid(), "已有备忘", TestDatabase.Now, "Asia/Shanghai",
            CaptureParseStatus.Pending, null, TestDatabase.Now);
        captures.Add(oldCapture);

        new MigrationRunner(db.Factory).Migrate();
        Assert.Equal(oldCapture, captures.GetById(oldCapture.Id));
        var store = new SqliteDigitalAssetStore(db.Factory);
        Assert.Empty(store.List());

        var date = new DateOnly(2026, 12, 3);
        var server = new DigitalAsset(Guid.NewGuid(), "云服务器", "服务器", new Uri("https://example.com/console"),
            "续费前检查规格\n生产环境", date, new TimeOnly(8, 30, 15));
        var domain = new DigitalAsset(Guid.NewGuid(), "个人域名", "域名", null, "", date, null);
        var membership = new DigitalAsset(Guid.NewGuid(), "网盘会员", "账号会员", null, "", null, null);

        store.Add(membership);
        store.Add(domain);
        store.Add(server);
        Assert.Equal(new[] { server, domain, membership }, new SqliteDigitalAssetStore(db.Factory).List());

        var renewed = server with { ExpirationDate = date.AddYears(1), ExpirationTime = null, Link = null };
        Assert.True(store.Update(renewed));
        Assert.Equal(new[] { domain, renewed, membership }, new SqliteDigitalAssetStore(db.Factory).List());

        Assert.True(store.Delete(server.Id));
        Assert.False(store.Update(renewed));
        Assert.Equal(new[] { domain, membership }, new SqliteDigitalAssetStore(db.Factory).List());
    }
}
