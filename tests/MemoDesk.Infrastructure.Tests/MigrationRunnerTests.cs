using System.Globalization;
using MemoDesk.Infrastructure.Storage.Migrations;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Infrastructure.Tests;

public sealed class MigrationRunnerTests
{
    [Fact]
    public void Migrate_FreshDatabase_EndsAtLatestVersionAndCreatesTables()
    {
        using var db = new TempDatabase();
        var runner = new MigrationRunner(db.Factory);

        runner.Migrate();

        Assert.Equal(MigrationsCatalog.All.Max(m => m.Version), runner.CurrentVersion());
        using var connection = db.Factory.Create();
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM app_meta;"));
    }

    [Fact]
    public void Migrate_CalledTwice_IsIdempotent()
    {
        using var db = new TempDatabase();
        var runner = new MigrationRunner(db.Factory);

        runner.Migrate();
        runner.Migrate();

        Assert.Equal(MigrationsCatalog.All.Max(m => m.Version), runner.CurrentVersion());
        using var connection = db.Factory.Create();
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM app_meta;"));
    }

    [Fact]
    public void Migrate_WithExtendedCatalog_AppliesOnlyPendingVersions()
    {
        using var db = new TempDatabase();
        var v1 = new SqlMigration(1, "v1", "CREATE TABLE t1 (id INTEGER PRIMARY KEY);");
        var v2 = new SqlMigration(2, "v2", "CREATE TABLE t2 (id INTEGER PRIMARY KEY);");

        new MigrationRunner(db.Factory, new[] { v1 }).Migrate();
        var runner = new MigrationRunner(db.Factory, new[] { v1, v2 });
        runner.Migrate();

        Assert.Equal(2, runner.CurrentVersion());
        using var connection = db.Factory.Create();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM t2;"));
    }

    [Fact]
    public void Migrate_UpgradesExistingV1DatabaseToLatest()
    {
        using var db = new TempDatabase();
        var v1 = MigrationsCatalog.All.Single(m => m.Version == 1);

        // 模拟 M0 版本创建的数据库。
        new MigrationRunner(db.Factory, new[] { v1 }).Migrate();

        var runner = new MigrationRunner(db.Factory);
        runner.Migrate();

        Assert.Equal(MigrationsCatalog.All.Max(m => m.Version), runner.CurrentVersion());
        using var connection = db.Factory.Create();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM captures;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM app_meta;"));
    }

    [Fact]
    public void Migrate_UpgradesExistingV7Database_CancelsLegacyReminderInstances()
    {
        using var db = new TempDatabase();

        // 先用 v1–v7 建库，模拟验收时的旧库。
        var legacyMigrations = MigrationsCatalog.All.Where(m => m.Version <= 7).ToArray();
        new MigrationRunner(db.Factory, legacyMigrations).Migrate();

        var itemId = Guid.NewGuid().ToString("N");
        var reminderId = Guid.NewGuid().ToString("N");
        var occurrenceId = Guid.NewGuid().ToString("N");
        var now = "2026-10-22T02:00:00.0000000+00:00";

        using (var connection = db.Factory.Create())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO items (id, type, content, tags_json, status, created_at_utc, updated_at_utc)
                VALUES ($itemId, 'Todo', 'legacy', '[]', 'Open', $now, $now);
                INSERT INTO reminders (id, item_id, reminder_at_utc, enabled, plan_version, created_at_utc, updated_at_utc)
                VALUES ($reminderId, $itemId, $now, 1, 2, $now, $now);
                INSERT INTO reminder_occurrences (
                    id, reminder_id, item_id, scheduled_at_utc, status, notification_tag,
                    options_snapshot_json, requested_at_utc, created_at_utc, updated_at_utc)
                VALUES ($occurrenceId, $reminderId, $itemId, $now, 'Requested', 'legacy-tag',
                        '[]', $now, $now, $now);
                INSERT INTO timeline_events (id, item_id, event_type, occurred_at_utc, recorded_at_utc, content_snapshot)
                VALUES ($eventId, $itemId, 'WorkOccurred', $now, $now, 'legacy work');
                """;
            command.Parameters.AddWithValue("$itemId", itemId);
            command.Parameters.AddWithValue("$reminderId", reminderId);
            command.Parameters.AddWithValue("$occurrenceId", occurrenceId);
            command.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }

        new MigrationRunner(db.Factory).Migrate();

        using var verify = db.Factory.Create();
        using var statusCommand = verify.CreateCommand();
        statusCommand.CommandText = "SELECT status, plan_version, cancel_reason FROM reminder_occurrences WHERE id = $id;";
        statusCommand.Parameters.AddWithValue("$id", occurrenceId);
        using var reader = statusCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Cancelled", reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1)); // 绝不继承当前计划版本
        Assert.Equal("legacy-version-unknown", reader.GetString(2));

        reader.Close();

        using var countCommand = verify.CreateCommand();
        countCommand.CommandText = "SELECT (SELECT COUNT(*) FROM timeline_events WHERE event_type = 'WorkOccurred'), (SELECT COUNT(*) FROM reminders WHERE enabled = 1);";
        using var countReader = countCommand.ExecuteReader();
        Assert.True(countReader.Read());
        Assert.Equal(0L, countReader.GetInt64(0));
        Assert.Equal(1L, countReader.GetInt64(1)); // 计划仍启用，下一轮扫描会按当前版本重建实例
    }

    [Fact]
    public void Migrate_V8ToV9_PrefersHandedOffDuplicate_AndBuildsUniqueIndex()
    {
        using var db = new TempDatabase();
        var legacy = MigrationsCatalog.All.Where(m => m.Version <= 8).ToArray();
        new MigrationRunner(db.Factory, legacy).Migrate();

        var itemId = Guid.NewGuid().ToString("N");
        var reminderId = Guid.NewGuid().ToString("N");
        var pendingId = Guid.NewGuid().ToString("N");
        var requestedId = Guid.NewGuid().ToString("N");
        var now = "2026-10-22T02:00:00.0000000+00:00";

        using (var connection = db.Factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO items (id, type, content, tags_json, status, created_at_utc, updated_at_utc)
                VALUES ($itemId, 'Todo', 'duplicate-plan', '[]', 'Open', $now, $now);
                INSERT INTO reminders (id, item_id, reminder_at_utc, enabled, plan_version, created_at_utc, updated_at_utc)
                VALUES ($reminderId, $itemId, $now, 1, 5, $now, $now);
                INSERT INTO reminder_occurrences (
                    id, reminder_id, item_id, scheduled_at_utc, status, notification_tag,
                    options_snapshot_json, created_at_utc, updated_at_utc, plan_version, handed_off_at_utc)
                VALUES ($pendingId, $reminderId, $itemId, $now, 'Pending', 'pending-tag',
                        '[]', '2026-10-22T01:00:00.0000000+00:00', $now, 5, NULL);
                INSERT INTO reminder_occurrences (
                    id, reminder_id, item_id, scheduled_at_utc, status, notification_tag,
                    options_snapshot_json, created_at_utc, updated_at_utc, plan_version, handed_off_at_utc)
                VALUES ($requestedId, $reminderId, $itemId, $now, 'Requested', 'requested-tag',
                        '[]', '2026-10-22T01:30:00.0000000+00:00', $now, 5, $now);
                """;
            command.Parameters.AddWithValue("$itemId", itemId);
            command.Parameters.AddWithValue("$reminderId", reminderId);
            command.Parameters.AddWithValue("$pendingId", pendingId);
            command.Parameters.AddWithValue("$requestedId", requestedId);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }

        new MigrationRunner(db.Factory).Migrate();

        using var verify = db.Factory.Create();

        string StatusOf(string id)
        {
            using var command = verify.CreateCommand();
            command.CommandText = "SELECT status || '|' || plan_version FROM reminder_occurrences WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return (string)command.ExecuteScalar()!;
        }

        Assert.StartsWith("Requested|5", StatusOf(requestedId));
        Assert.StartsWith("Cancelled|-", StatusOf(pendingId));

        // 唯一索引拒绝同计划版本再次插入。
        using var duplicate = verify.CreateCommand();
        duplicate.CommandText = """
            INSERT INTO reminder_occurrences (
                id, reminder_id, item_id, scheduled_at_utc, status, notification_tag,
                options_snapshot_json, created_at_utc, updated_at_utc, plan_version)
            VALUES ($id, $reminderId, $itemId, $now, 'Pending', 'another-tag',
                    '[]', $now, $now, 5);
            """;
        duplicate.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        duplicate.Parameters.AddWithValue("$reminderId", reminderId);
        duplicate.Parameters.AddWithValue("$itemId", itemId);
        duplicate.Parameters.AddWithValue("$now", now);
        Assert.ThrowsAny<SqliteException>(() => duplicate.ExecuteNonQuery());
    }

    [Fact]
    public void Migrate_V8ToV9_PrefersFailedOverPending()
    {
        using var db = new TempDatabase();
        var legacy = MigrationsCatalog.All.Where(m => m.Version <= 8).ToArray();
        new MigrationRunner(db.Factory, legacy).Migrate();

        var itemId = Guid.NewGuid().ToString("N");
        var reminderId = Guid.NewGuid().ToString("N");
        var failedId = Guid.NewGuid().ToString("N");
        var pendingId = Guid.NewGuid().ToString("N");
        var now = "2026-10-22T02:00:00.0000000+00:00";

        using (var connection = db.Factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO items (id, type, content, tags_json, status, created_at_utc, updated_at_utc)
                VALUES ($itemId, 'Todo', 'duplicate-failed', '[]', 'Open', $now, $now);
                INSERT INTO reminders (id, item_id, reminder_at_utc, enabled, plan_version, created_at_utc, updated_at_utc)
                VALUES ($reminderId, $itemId, $now, 1, 3, $now, $now);
                INSERT INTO reminder_occurrences (
                    id, reminder_id, item_id, scheduled_at_utc, status, notification_tag,
                    options_snapshot_json, created_at_utc, updated_at_utc, plan_version)
                VALUES ($failedId, $reminderId, $itemId, $now, 'Failed', 'failed-tag',
                        '[]', '2026-10-22T01:00:00.0000000+00:00', $now, 3);
                INSERT INTO reminder_occurrences (
                    id, reminder_id, item_id, scheduled_at_utc, status, notification_tag,
                    options_snapshot_json, created_at_utc, updated_at_utc, plan_version)
                VALUES ($pendingId, $reminderId, $itemId, $now, 'Pending', 'pending-tag',
                        '[]', '2026-10-22T01:30:00.0000000+00:00', $now, 3);
                """;
            command.Parameters.AddWithValue("$itemId", itemId);
            command.Parameters.AddWithValue("$reminderId", reminderId);
            command.Parameters.AddWithValue("$failedId", failedId);
            command.Parameters.AddWithValue("$pendingId", pendingId);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }

        new MigrationRunner(db.Factory).Migrate();

        using var verify = db.Factory.Create();
        using var status = verify.CreateCommand();
        status.CommandText = "SELECT status FROM reminder_occurrences WHERE id = $failed;";
        status.Parameters.AddWithValue("$failed", failedId);
        Assert.Equal("Failed", (string)status.ExecuteScalar()!);
    }

    [Fact]
    public void Migrate_FailedMigrationScript_DoesNotAdvanceVersion()
    {
        using var db = new TempDatabase();
        var good = new SqlMigration(1, "v1", "CREATE TABLE t1 (id INTEGER PRIMARY KEY);");
        var bad = new SqlMigration(2, "v2", "CREATE TABLE t2 (id INTEGER PRIMARY KEY); THIS IS NOT SQL;");

        var runner = new MigrationRunner(db.Factory, new[] { good, bad });

        Assert.ThrowsAny<SqliteException>(() => runner.Migrate());
        Assert.Equal(1, new MigrationRunner(db.Factory, new[] { good }).CurrentVersion());
    }

    [Fact]
    public void Constructor_WithGapInVersions_Throws()
    {
        using var db = new TempDatabase();
        var migrations = new[]
        {
            new SqlMigration(1, "v1", "SELECT 1;"),
            new SqlMigration(3, "v3", "SELECT 1;"),
        };

        Assert.Throws<ArgumentException>(() => new MigrationRunner(db.Factory, migrations));
    }

    [Fact]
    public void Constructor_NotStartingAtOne_Throws()
    {
        using var db = new TempDatabase();
        var migrations = new[] { new SqlMigration(2, "v2", "SELECT 1;") };

        Assert.Throws<ArgumentException>(() => new MigrationRunner(db.Factory, migrations));
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
