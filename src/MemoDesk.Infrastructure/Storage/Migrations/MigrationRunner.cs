using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Infrastructure.Storage.Migrations;

/// <summary>
/// 基于 <c>PRAGMA user_version</c> 的增量迁移。每个迁移与其版本号在同一个事务中提交，
/// 版本号只升不降；已应用的版本不会重复执行（迁移脚本本身也应保持幂等友好）。
/// </summary>
public sealed class MigrationRunner
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IReadOnlyList<SqlMigration> _migrations;

    public MigrationRunner(SqliteConnectionFactory connectionFactory)
        : this(connectionFactory, MigrationsCatalog.All)
    {
    }

    public MigrationRunner(SqliteConnectionFactory connectionFactory, IReadOnlyList<SqlMigration> migrations)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        Validate(migrations);
        _migrations = migrations;
    }

    /// <summary>当前数据库结构版本；0 表示尚未迁移。</summary>
    public int CurrentVersion()
    {
        using var connection = _connectionFactory.Create();
        return GetUserVersion(connection);
    }

    /// <summary>把数据库迁移到最新版本。</summary>
    public void Migrate()
    {
        using var connection = _connectionFactory.Create();
        var currentVersion = GetUserVersion(connection);

        foreach (var migration in _migrations.OrderBy(m => m.Version))
        {
            if (migration.Version <= currentVersion)
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = string.Create(
                    CultureInfo.InvariantCulture,
                    $"PRAGMA user_version = {migration.Version};");
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            currentVersion = migration.Version;
        }
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Validate(IReadOnlyList<SqlMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        if (migrations.Count == 0)
        {
            throw new ArgumentException("至少需要一个迁移脚本。", nameof(migrations));
        }

        var ordered = migrations.OrderBy(m => m.Version).ToList();
        if (ordered[0].Version != 1)
        {
            throw new ArgumentException("迁移版本必须从 1 开始。", nameof(migrations));
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Version != ordered[i - 1].Version + 1)
            {
                throw new ArgumentException("迁移版本必须连续且唯一。", nameof(migrations));
            }
        }
    }
}
