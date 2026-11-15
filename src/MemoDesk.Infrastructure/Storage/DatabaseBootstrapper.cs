using MemoDesk.Infrastructure.Storage.Migrations;

namespace MemoDesk.Infrastructure.Storage;

/// <summary>应用启动时初始化本地数据库的入口。</summary>
public sealed class DatabaseBootstrapper
{
    private readonly MigrationRunner _migrationRunner;
    private readonly LegacyMemoMigration _legacyMemoMigration;
    private readonly DatabaseBackupService? _backups;

    public DatabaseBootstrapper(MigrationRunner migrationRunner, LegacyMemoMigration legacyMemoMigration,
        DatabaseBackupService? backups = null)
    {
        _migrationRunner = migrationRunner ?? throw new ArgumentNullException(nameof(migrationRunner));
        _legacyMemoMigration = legacyMemoMigration ?? throw new ArgumentNullException(nameof(legacyMemoMigration));
        _backups = backups;
    }

    public void Initialize()
    {
        var version = _migrationRunner.CurrentVersion();
        if (version > 0 && version < MigrationsCatalog.All.Max(m => m.Version))
            _backups?.BackupBeforeUpgrade(version);
        _migrationRunner.Migrate();
        _legacyMemoMigration.Run();
        _backups?.RefreshDailyBackup();
    }

    public int GetSchemaVersion() => _migrationRunner.CurrentVersion();
}
