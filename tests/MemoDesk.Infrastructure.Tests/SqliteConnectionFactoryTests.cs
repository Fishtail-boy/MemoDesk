using System.Globalization;
using MemoDesk.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Infrastructure.Tests;

public sealed class SqliteConnectionFactoryTests
{
    [Fact]
    public void Create_AppliesExpectedPragmas()
    {
        using var db = new TempDatabase();
        using var connection = db.Factory.Create();

        Assert.Equal("wal", ScalarString(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, ScalarLong(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(2L, ScalarLong(connection, "PRAGMA synchronous;")); // 2 = FULL
        Assert.Equal(5000L, ScalarLong(connection, "PRAGMA busy_timeout;"));
    }

    [Fact]
    public void Create_EnsuresParentDirectoryExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "MemoDesk.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "nested", "memo.db");
        var factory = new SqliteConnectionFactory(path);

        using (var connection = factory.Create())
        {
            Assert.True(File.Exists(path));
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
