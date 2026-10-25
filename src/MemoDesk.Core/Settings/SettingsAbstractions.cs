namespace MemoDesk.Core.Settings;

/// <summary>键值设置存储（设计 7.7）。敏感值由调用方先经 <see cref="ISecretProtector"/> 处理。</summary>
public interface ISettingsStore
{
    string? Get(string key);

    void Set(string key, string value);

    void Remove(string key);

    IReadOnlyDictionary<string, string> GetAll();
}

/// <summary>敏感值保护（Windows 实现为 DPAPI 当前用户范围，设计 6.1）。</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}
