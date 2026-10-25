using MemoDesk.Infrastructure.Storage.Migrations;

namespace MemoDesk.Infrastructure.Storage;

/// <summary>应用启动时初始化本地数据库的入口。</summary>
public sealed class DatabaseBootstrapper
{
    private readonly MigrationRunner _migrationRunner;
    private readonly LegacyMemoMigration _legacyMemoMigration;

    public DatabaseBootstrapper(MigrationRunner migrationRunner, LegacyMemoMigration legacyMemoMigration)
    {
        _migrationRunner = migrationRunner ?? throw new ArgumentNullException(nameof(migrationRunner));
        _legacyMemoMigration = legacyMemoMigration ?? throw new ArgumentNullException(nameof(legacyMemoMigration));
    }

    public void Initialize()
    {
        _migrationRunner.Migrate();
        _legacyMemoMigration.Run();
    }

    public int GetSchemaVersion() => _migrationRunner.CurrentVersion();
}
