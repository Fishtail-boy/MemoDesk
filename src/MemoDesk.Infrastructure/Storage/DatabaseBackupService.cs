using Microsoft.Data.Sqlite;
using System.Globalization;

namespace MemoDesk.Infrastructure.Storage;

/// <summary>Consistent online snapshots include committed WAL data. Never copy an open SQLite file.</summary>
public sealed class DatabaseBackupService(SqliteConnectionFactory factory, TimeProvider clock)
{
    private readonly object _gate = new();
    public string BackupDirectory => Path.Combine(Path.GetDirectoryName(factory.DatabasePath)!, "backups");
    public string? LastBackupPath { get; private set; }
    public bool BackupFailed { get; private set; }

    public void Export(string path)
    {
        lock (_gate)
        {
            var target = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(target), ".memobak", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("备忘备份文件须使用 .memobak 扩展名。", nameof(path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var source = factory.Create())
                using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = temp, Pooling = false }.ToString()))
                {
                    destination.Open();
                    source.BackupDatabase(destination);
                    using var check = destination.CreateCommand();
                    check.CommandText = "PRAGMA journal_mode=DELETE;";
                    check.ExecuteNonQuery();
                    check.CommandText = "PRAGMA quick_check;";
                    if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                        throw new InvalidDataException("备忘备份校验失败。");
                }
                using (var file = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    file.Flush(flushToDisk: true);
                File.Move(temp, target, overwrite: true);
                LastBackupPath = target;
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    public void BackupBeforeUpgrade(int version)
    {
        lock (_gate)
        {
            var target = Path.Combine(BackupDirectory, $"before-v{version}-{clock.GetUtcNow():yyyyMMddHHmmss}-{Guid.NewGuid():N}.memobak");
            Export(target);
            Retain("before-v*.memobak", 5, target);
        }
    }

    public void RefreshDailyBackup()
    {
        lock (_gate)
        {
            try
            {
                Export(Path.Combine(BackupDirectory, $"daily-{clock.GetLocalNow():yyyyMMdd}.memobak"));
                Retain("daily-*.memobak", 14);
                BackupFailed = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                BackupFailed = true;
            }
        }
    }

    private void Retain(string pattern, int count, string? preserve = null)
    {
        foreach (var file in Directory.EnumerateFiles(BackupDirectory, pattern)
            .OrderByDescending(file => string.Equals(file, preserve, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(BackupTime).ThenByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(count)) File.Delete(file);
    }

    private static DateTimeOffset BackupTime(string path)
    {
        var parts = Path.GetFileNameWithoutExtension(path).Split('-');
        var timestamp = parts switch
        {
            ["before", _, var time, _] => time,
            ["daily", var date] => date,
            _ => string.Empty,
        };
        return DateTimeOffset.TryParseExact(timestamp, ["yyyyMMddHHmmss", "yyyyMMdd"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTime)
            ? parsedTime : File.GetLastWriteTimeUtc(path);
    }
}
