using MemoDesk.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Infrastructure.Tests;

/// <summary>每个测试独立的临时数据库，Dispose 时清理连接池与目录。</summary>
internal sealed class TempDatabase : IDisposable
{
    public TempDatabase()
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), "MemoDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootDirectory);
        DatabasePath = Path.Combine(RootDirectory, "memo.db");
        Factory = new SqliteConnectionFactory(DatabasePath);
    }

    public string RootDirectory { get; }

    public string DatabasePath { get; }

    public SqliteConnectionFactory Factory { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(RootDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
