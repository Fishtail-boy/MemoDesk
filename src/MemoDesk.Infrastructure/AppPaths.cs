namespace MemoDesk.Infrastructure;

/// <summary>
/// 应用在当前用户下的数据目录约定：数据库、日志与安装文件分离（设计 9）。
/// </summary>
public sealed class AppPaths
{
    public const string AppDirectoryName = "MemoDesk";

    public AppPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    /// <summary>应用数据根目录。</summary>
    public string RootDirectory { get; }

    /// <summary>主数据库文件。</summary>
    public string DatabasePath => Path.Combine(RootDirectory, "memo.db");

    /// <summary>滚动日志目录。</summary>
    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    public static AppPaths ForCurrentUser() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDirectoryName));

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
