namespace MemoDesk.Vault.Core;

public enum VaultError { Locked, InvalidPassword, InvalidData, InvalidMapping, Conflict, AlreadyExists, NotFound, TooLarge, Storage, Cancelled, InvalidPasswordFormat }

/// <summary>只携带固定错误码；不将输入、SQL 或底层异常传播到宿主日志。</summary>
public sealed class VaultException(VaultError error) : Exception(error.ToString())
{
    public VaultError Error { get; } = error;
}

public sealed record VaultEntrySummary(
    Guid Id, long RowVersion, string Title, string UserName, string Url, string Category,
    string Tags, bool Favorite, bool IsDeleted, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public override string ToString() => $"VaultEntry:{Id}";
}

/// <summary>仅用于当前动作。释放时解除明文引用；不承诺清除不可变字符串的底层内存。</summary>
public sealed class VaultSecret(string password, string notes) : IDisposable
{
    public string Password { get; private set; } = password;
    public string Notes { get; private set; } = notes;
    public void Dispose() { Password = string.Empty; Notes = string.Empty; }
    public override string ToString() => "VaultSecret:[redacted]";
}

public sealed class VaultEntryDraft : IDisposable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long ExpectedVersion { get; set; }
    public string Title { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Category { get; set; } = "个人";
    public string Tags { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool Favorite { get; set; }
    public void Validate()
    {
        if (Id == Guid.Empty || ExpectedVersion < 0 || string.IsNullOrWhiteSpace(Title))
            throw new VaultException(VaultError.InvalidData);
        if (new[] { Title, UserName, Url, Category, Tags }.Any(x => x is null || x.Length > 2048) ||
            Password is null || Notes is null || Password.Length > 65536 || Notes.Length > 65536)
            throw new VaultException(VaultError.TooLarge);
    }
    public void Dispose() { Title = UserName = Password = Url = Category = Tags = Notes = string.Empty; }
    public override string ToString() => "VaultEntryDraft:[redacted]";
}

public interface IVaultService : IDisposable
{
    bool Exists { get; }
    bool IsUnlocked { get; }
    long SessionVersion { get; }
    event Action? Locked;
    void Create(string masterPassword, long? expectedSessionVersion = null);
    void Unlock(string masterPassword, long? expectedSessionVersion = null);
    void Lock();
    IReadOnlyList<VaultEntrySummary> List();
    VaultSecret ReadSecret(Guid id, long expectedVersion);
    void Save(VaultEntryDraft draft);
    void SetDeleted(Guid id, long expectedVersion, bool deleted);
    void DeletePermanently(Guid id, long expectedVersion);
    int Import(IReadOnlyList<VaultEntryDraft> entries, Guid batchId);
    void ChangeMasterPassword(string currentPassword, string newPassword);
    void ExportBackup(string path);
    void RestoreBackup(string path, string masterPassword, long? expectedSessionVersion = null);
    string? LastBackupPath { get; }
    bool BackupFailed { get; }
}

public static class VaultSearch
{
    public static IReadOnlyList<VaultEntrySummary> Filter(IEnumerable<VaultEntrySummary> entries,
        string query = "", string category = "全部", bool favoritesOnly = false, bool deleted = false)
    {
        var term = query.Trim();
        return entries.Where(e => e.IsDeleted == deleted && (!favoritesOnly || e.Favorite) &&
                (category == "全部" || e.Category == category) &&
                (term.Length == 0 || new[] { e.Title, e.UserName, e.Url, e.Category, e.Tags }
                    .Any(v => v.Contains(term, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(e => e.Favorite).ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}

public interface ISecureClipboard
{
    bool Copy(string value);
    void ClearOwned();
}

public sealed class VaultActivity(TimeProvider clock)
{
    private long _lastActivity = clock.GetTimestamp();
    public void Touch() => _lastActivity = clock.GetTimestamp();
    public bool HasExpired(TimeSpan timeout) => clock.GetElapsedTime(_lastActivity) >= timeout;
}
