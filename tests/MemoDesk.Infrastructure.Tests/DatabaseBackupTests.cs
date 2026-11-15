using MemoDesk.Infrastructure.Storage;
using MemoDesk.Infrastructure.Storage.Migrations;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Infrastructure.Tests;

public sealed class DatabaseBackupTests
{
    [Theory]
    [InlineData(9, 10)]
    [InlineData(10, 11)]
    [InlineData(99, 100)]
    public void UpgradeRetentionUsesTimeAcrossSchemaVersions(int oldVersion, int newVersion)
    {
        using var db = new TestDatabase();
        var clock = new BackupClock();
        var backups = new DatabaseBackupService(db.Factory, clock);
        for (var i = 0; i < 5; i++)
        {
            clock.Now = TestDatabase.Now.AddDays(i);
            backups.BackupBeforeUpgrade(oldVersion);
        }
        var oldest = Directory.GetFiles(backups.BackupDirectory, $"before-v{oldVersion}-{TestDatabase.Now:yyyyMMddHHmmss}-*.memobak").Single();
        // Copy/restore operations may change filesystem timestamps: use the embedded snapshot time.
        File.SetLastWriteTimeUtc(oldest, TestDatabase.Now.AddYears(1).UtcDateTime);
        clock.Now = TestDatabase.Now.AddDays(5);
        db.SettingsStore.Set("upgrade-marker", "latest snapshot");
        backups.BackupBeforeUpgrade(newVersion);
        Assert.Equal(5, Directory.GetFiles(backups.BackupDirectory, "before-v*.memobak").Length);
        Assert.False(File.Exists(oldest));
        var latest = Assert.Single(Directory.GetFiles(backups.BackupDirectory, $"before-v{newVersion}-*.memobak"));
        using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = latest, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        snapshot.Open();
        using var query = snapshot.CreateCommand();
        query.CommandText = "SELECT value FROM app_settings WHERE key='upgrade-marker'";
        Assert.Equal("latest snapshot", query.ExecuteScalar());
    }

    [Fact]
    public void CurrentUpgradeSnapshotSurvivesEqualTimestampsAndClockRollback()
    {
        using var db = new TestDatabase();
        var clock = new BackupClock();
        var backups = new DatabaseBackupService(db.Factory, clock);
        for (var i = 0; i < 6; i++) backups.BackupBeforeUpgrade(9);
        clock.Now = TestDatabase.Now.AddDays(-1);
        backups.BackupBeforeUpgrade(10);
        Assert.Equal(5, Directory.GetFiles(backups.BackupDirectory, "before-v*.memobak").Length);
        Assert.Single(Directory.GetFiles(backups.BackupDirectory, "before-v10-*.memobak"));
    }

    [Fact]
    public void ManualAndUpgradeExportsDoNotHideAutomaticBackupFailure()
    {
        using var db = new TestDatabase();
        var clock = new BackupClock();
        var backups = new DatabaseBackupService(db.Factory, clock);
        backups.RefreshDailyBackup();
        var daily = backups.LastBackupPath!;
        using (File.Open(daily, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            backups.RefreshDailyBackup();
            Assert.True(backups.BackupFailed);
            var export = Path.Combine(Path.GetDirectoryName(db.Factory.DatabasePath)!, "manual-export.memobak");
            backups.Export(export);
            Assert.True(File.Exists(export));
            Assert.True(backups.BackupFailed);
            backups.BackupBeforeUpgrade(11);
            Assert.True(backups.BackupFailed);
            backups.RefreshDailyBackup();
            Assert.True(backups.BackupFailed);
        }
        backups.RefreshDailyBackup();
        Assert.False(backups.BackupFailed);
    }

    [Fact]
    public void SnapshotIncludesCommittedWalAndCanBeReopenedIndependently()
    {
        using var db = new TestDatabase();
        // Keep a reader/connection alive so the committed changes remain in WAL.
        using var live = db.Factory.Create();
        db.SettingsStore.Set("synthetic-marker", "latest committed value");
        var backups = new DatabaseBackupService(db.Factory, TimeProvider.System);
        var path = Path.Combine(Path.GetDirectoryName(db.Factory.DatabasePath)!, "export.memobak");
        backups.Export(path);
        db.SettingsStore.Set("synthetic-marker", "changed after backup");
        using var restored = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        restored.Open();
        using var query = restored.CreateCommand();
        query.CommandText = "SELECT value FROM app_settings WHERE key='synthetic-marker'";
        Assert.Equal("latest committed value", query.ExecuteScalar());
        query.CommandText = "PRAGMA quick_check";
        Assert.Equal("ok", query.ExecuteScalar());
        Assert.False(File.Exists(path + "-wal"));
    }

    [Fact]
    public void AutomaticBackupRetainsFourteenDatesAndReportsFailureThenRecovers()
    {
        using var db = new TestDatabase();
        var clock = new BackupClock();
        var backups = new DatabaseBackupService(db.Factory, clock);
        for (var i = 0; i < 20; i++) { clock.Now = TestDatabase.Now.AddDays(i); backups.RefreshDailyBackup(); }
        Assert.Equal(14, Directory.GetFiles(backups.BackupDirectory, "daily-*.memobak").Length);
        Assert.False(backups.BackupFailed);
        var next = Path.Combine(backups.BackupDirectory, $"daily-{clock.GetLocalNow():yyyyMMdd}.memobak");
        using (File.Open(next, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            backups.RefreshDailyBackup();
            Assert.True(backups.BackupFailed);
        }
        backups.RefreshDailyBackup();
        Assert.False(backups.BackupFailed);
        Assert.Empty(Directory.GetFiles(backups.BackupDirectory, "*.tmp-*"));
    }

    [Fact]
    public void BootstrapPreservesDatabaseBeforeUpgrade()
    {
        using var db = new TempDatabase();
        new MigrationRunner(db.Factory, MigrationsCatalog.All.Where(m => m.Version <= 9).ToArray()).Migrate();
        var backups = new DatabaseBackupService(db.Factory, TimeProvider.System);
        new DatabaseBootstrapper(new MigrationRunner(db.Factory), new LegacyMemoMigration(db.Factory, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LegacyMemoMigration>.Instance), backups).Initialize();
        var path = Assert.Single(Directory.GetFiles(backups.BackupDirectory, "before-v9-*.memobak"));
        using var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        old.Open();
        using var cmd = old.CreateCommand(); cmd.CommandText = "PRAGMA user_version";
        Assert.Equal(9L, cmd.ExecuteScalar());
        Assert.Equal(MigrationsCatalog.All.Max(m => m.Version), new MigrationRunner(db.Factory).CurrentVersion());
    }

    [Fact]
    public void OlderAppRefusesNewerDatabaseWithoutMutation()
    {
        using var db = new TempDatabase();
        new MigrationRunner(db.Factory).Migrate();
        using var live = db.Factory.Create();
        using var cmd = live.CreateCommand(); cmd.CommandText = "PRAGMA user_version=999;"; cmd.ExecuteNonQuery();
        Assert.Throws<InvalidOperationException>(() => new MigrationRunner(db.Factory).Migrate());
        cmd.CommandText = "PRAGMA user_version";
        Assert.Equal(999L, cmd.ExecuteScalar());
    }

    [Fact]
    public void ExportCannotOverwriteLiveDatabase()
    {
        using var db = new TestDatabase();
        var backups = new DatabaseBackupService(db.Factory, TimeProvider.System);
        Assert.Throws<ArgumentException>(() => backups.Export(db.Factory.DatabasePath));
    }

    private sealed class BackupClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = TestDatabase.Now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
