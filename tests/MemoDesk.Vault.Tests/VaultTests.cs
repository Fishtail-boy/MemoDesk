using System.Text;
using System.Text.Json.Nodes;
using MemoDesk.Vault.Core;
using MemoDesk.Vault.Infrastructure;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Vault.Tests;

public sealed class VaultTests : IDisposable
{
    private const string Password = "  测试主口令-Example-2026  ";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MemoDesk.Vault.Tests", Guid.NewGuid().ToString("N"));
    private readonly List<VaultService> _services = [];
    public VaultTests() => Directory.CreateDirectory(_root);
    private VaultService New(string name = "vault.db")
    { var s = new VaultService(Path.Combine(_root, name)); _services.Add(s); return s; }
    private static VaultEntryDraft Draft(string title = "synthetic-账户-Title") => new()
    {
        Title = title, UserName = "synthetic-user@example.invalid", Password = " synthetic-secret-密码-123 ",
        Url = "https://synthetic.invalid/private", Category = "工作", Tags = "项目A", Notes = "synthetic-保密备注"
    };
    private void Sql(string sql)
    {
        using var c = new SqliteConnection($"Data Source={Path.Combine(_root, "vault.db")};Pooling=False");
        c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }
    [Fact] public void RoundTripPreservesUnicodeWhitespaceAndSurvivesRestart()
    {
        var v = New(); v.Create(Password); using var draft = Draft(); v.Save(draft); v.Lock();
        var reopened = New(); reopened.Unlock(Password); var row = Assert.Single(reopened.List());
        using var secret = reopened.ReadSecret(row.Id, row.RowVersion);
        Assert.Equal(draft.Password, secret.Password); Assert.Equal(draft.Notes, secret.Notes);
        Assert.Equal(draft.Title, row.Title); Assert.Equal(1, row.RowVersion);
    }
    [Fact] public void WrongPasswordAndTrimmedPasswordCannotUnlock()
    {
        var v = New(); v.Create(Password); v.Lock();
        Assert.Equal(VaultError.InvalidPassword, Assert.Throws<VaultException>(() => v.Unlock(Password.Trim())).Error);
        Assert.False(v.IsUnlocked); Assert.Throws<VaultException>(() => v.List());
    }
    [Fact] public void PlaintextIsAbsentFromDatabaseBackupsAndJournals()
    {
        var v = New(); v.Create(Password); using var d = Draft(); v.Save(d);
        v.ExportBackup(Path.Combine(_root, "copy.memovault")); v.Lock();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            foreach (var value in new[] { d.Title, d.UserName, d.Password, d.Notes, d.Url, d.Tags, d.Category, Password })
            { Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0); Assert.False(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(value)) >= 0); }
        }
    }
    [Fact] public void UpdateUsesRevisionAndReencryptsBothPayloads()
    {
        var v = New(); v.Create(Password); using var d = Draft(); v.Save(d);
        Assert.Equal(VaultError.Conflict, Assert.Throws<VaultException>(() => v.Save(d)).Error);
        d.ExpectedVersion = 1; d.Password = "changed-secret"; v.Save(d);
        var row = Assert.Single(v.List()); Assert.Equal(2, row.RowVersion);
        Assert.Throws<VaultException>(() => v.ReadSecret(row.Id, 1));
        using var secret = v.ReadSecret(row.Id, 2); Assert.Equal("changed-secret", secret.Password);
    }
    [Fact] public void TrashRestoreAndPermanentDeleteAreDistinct()
    {
        var v = New(); v.Create(Password); using var d = Draft(); v.Save(d);
        Assert.Throws<VaultException>(() => v.DeletePermanently(d.Id, 1));
        v.SetDeleted(d.Id, 1, true); Assert.True(Assert.Single(v.List()).IsDeleted);
        Assert.Throws<VaultException>(() => v.ReadSecret(d.Id, 2));
        v.SetDeleted(d.Id, 2, false); Assert.False(Assert.Single(v.List()).IsDeleted);
        v.SetDeleted(d.Id, 3, true); v.DeletePermanently(d.Id, 4); Assert.Empty(v.List());
    }
    [Theory] [InlineData("metadata")] [InlineData("secret")]
    public void TamperedPayloadIsRejected(string column)
    {
        var v = New(); v.Create(Password); using var d = Draft(); v.Save(d); v.Lock();
        Sql($"UPDATE vault_entries SET {column}=zeroblob(length({column}))");
        Assert.Throws<VaultException>(() => v.Unlock(Password)); Assert.False(v.IsUnlocked);
    }
    [Fact] public void SwappingMetadataBetweenEntriesFailsIdentityAuthentication()
    {
        var v = New(); v.Create(Password); using var a = Draft("A"); using var b = Draft("B"); v.Save(a); v.Save(b); v.Lock();
        Sql($"UPDATE vault_entries SET metadata=(SELECT metadata FROM vault_entries WHERE id='{a.Id:N}') WHERE id='{b.Id:N}'");
        Assert.Throws<VaultException>(() => v.Unlock(Password));
    }
    [Fact] public void HostileKdfCostIsRejectedBeforeDerivation()
    {
        var v = New(); v.Create(Password); v.Lock();
        Sql("UPDATE vault_meta SET envelope=json_set(envelope,'$.Iterations',2147483647)");
        Assert.Equal(VaultError.InvalidData, Assert.Throws<VaultException>(() => v.Unlock(Password)).Error);
    }
    [Fact] public void DatabaseViewsAndUnexpectedSchemaAreRejected()
    {
        var v = New(); v.Create(Password); v.Lock();
        Sql("ALTER TABLE vault_meta RENAME TO hidden_meta; CREATE VIEW vault_meta AS SELECT * FROM hidden_meta");
        Assert.Equal(VaultError.InvalidData, Assert.Throws<VaultException>(() => v.Unlock(Password)).Error);
    }
    [Fact] public void IdleClockUsesMonotonicTimeAndUserActivity()
    {
        var clock = new Clock(); var activity = new VaultActivity(clock);
        clock.Elapsed = TimeSpan.FromMinutes(5); Assert.True(activity.HasExpired(TimeSpan.FromMinutes(5)));
        activity.Touch(); Assert.False(activity.HasExpired(TimeSpan.FromMinutes(5)));
        clock.Elapsed += TimeSpan.FromMinutes(1); Assert.True(activity.HasExpired(TimeSpan.FromMinutes(1)));
    }
    private sealed class Clock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
    }
    [Fact] public void NewDatabaseNeverOverwritesExistingFile()
    {
        var v = New(); v.Create(Password); var original = File.ReadAllBytes(Path.Combine(_root, "vault.db"));
        Assert.Throws<VaultException>(() => v.Create("different-password"));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_root, "vault.db")));
    }
    [Fact] public void ModifiedMasterPasswordRetainsEntriesAndOldBackupUsesOldPassword()
    {
        var v = New(); v.Create(Password); using var d = Draft(); v.Save(d);
        var backup = Path.Combine(_root, "old.memovault"); v.ExportBackup(backup);
        v.ChangeMasterPassword(Password, "new-master-password-2026"); v.Lock();
        Assert.Throws<VaultException>(() => v.Unlock(Password)); v.Unlock("new-master-password-2026"); Assert.Single(v.List());
        var other = New("other.db"); other.RestoreBackup(backup, Password); other.Unlock(Password); Assert.Single(other.List());
    }
    [Fact] public void BackupRestoresToIndependentDirectoryAndStartsLocked()
    {
        var v = New(); v.Create(Password); using var d = Draft(); v.Save(d);
        var backup = Path.Combine(_root, "transfer.memovault"); v.ExportBackup(backup);
        var target = New("different-profile/new.db"); target.RestoreBackup(backup, Password);
        Assert.False(target.IsUnlocked); target.Unlock(Password); Assert.Single(target.List());
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void FailedRestorePreservesCurrentVault(bool corrupt)
    {
        var v = New(); v.Create(Password); using var d = Draft(); v.Save(d);
        var backup = Path.Combine(_root, "bad.memovault"); v.ExportBackup(backup);
        if (corrupt) { var data = File.ReadAllBytes(backup); data[^1] ^= 1; File.WriteAllBytes(backup, data); }
        var before = File.ReadAllBytes(Path.Combine(_root, "vault.db"));
        Assert.Throws<VaultException>(() => v.RestoreBackup(backup, corrupt ? Password : "wrong-password"));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_root, "vault.db"))); Assert.Single(v.List());
    }
    [Fact] public void ImportIsTransactionalAndRepeatedBatchDoesNotDuplicate()
    {
        var v = New(); v.Create(Password); using var a = Draft("A"); using var b = Draft("B");
        var batch = Guid.NewGuid(); Assert.Equal(2, v.Import([a, b], batch)); Assert.Equal(0, v.Import([a, b], batch));
        Assert.Equal(2, v.List().Count);
        using var c = Draft("C"); using var conflict = Draft("conflict"); conflict.Id = a.Id;
        Assert.Throws<VaultException>(() => v.Import([c, conflict], Guid.NewGuid())); Assert.Equal(2, v.List().Count);
    }
    [Fact] public void LockInvalidatesSessionAndAllSecretOperations()
    {
        var v = New(); v.Create(Password); using var d = Draft(); v.Save(d); var version = v.SessionVersion;
        var locked = 0; v.Locked += () => locked++; v.Lock();
        Assert.True(v.SessionVersion > version); Assert.Equal(1, locked);
        Assert.Throws<VaultException>(() => v.ReadSecret(d.Id, 1)); Assert.Throws<VaultException>(() => v.Save(d));
        Assert.Throws<VaultException>(() => v.ExportBackup(Path.Combine(_root, "locked.memovault")));
    }
    [Fact] public async Task LockDuringUnlockCannotPublishAnUnlockedSession()
    {
        var v = New(); v.Create(Password); v.Lock();
        var task = Task.Run(() => { try { v.Unlock(Password); } catch (VaultException) { } });
        await Task.Delay(25); v.Lock(); await task; Assert.False(v.IsUnlocked);
    }
    [Fact] public async Task LockNeverWaitsForBlockedDatabaseWrite()
    {
        var v = New(); v.Create(Password);
        using var c = new SqliteConnection($"Data Source={Path.Combine(_root, "vault.db")};Pooling=False");
        c.Open(); using var tx = c.BeginTransaction();
        using var started = new ManualResetEventSlim();
        var save = Task.Run(() =>
        {
            using var d = Draft(); started.Set();
            try { v.Save(d); } catch (VaultException) { }
        });
        started.Wait(TimeSpan.FromSeconds(3)); await Task.Delay(60);
        var watch = System.Diagnostics.Stopwatch.StartNew(); v.Lock();
        Assert.False(v.IsUnlocked); Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500));
        tx.Rollback(); await save; v.Unlock(Password); Assert.Empty(v.List());
    }
    [Fact] public void SearchFiltersMetadataAndKeepsTrashSeparate()
    {
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var row = new VaultEntrySummary(id, 1, "GitHub", "User", "https://example.invalid", "工作", "研发", true, false, now, now);
        Assert.Single(VaultSearch.Filter([row], "github")); Assert.Single(VaultSearch.Filter([row], "研发"));
        Assert.Empty(VaultSearch.Filter([row], "", "个人")); Assert.Empty(VaultSearch.Filter([row with { IsDeleted = true }]));
    }
    [Fact] public void SecretObjectsNeverPrintTheirContents()
    {
        using var d = Draft(); using var secret = new VaultSecret(d.Password, d.Notes);
        Assert.DoesNotContain(d.Password, d.ToString()); Assert.DoesNotContain(d.Password, secret.ToString());
    }
    [Fact] public void QueuedUnlockFromRevokedSessionIsRejected()
    {
        var v = New(); v.Create(Password); v.Lock(); var queuedVersion = v.SessionVersion; v.Lock();
        Assert.Equal(VaultError.Cancelled, Assert.Throws<VaultException>(() => v.Unlock(Password, queuedVersion)).Error);
        Assert.False(v.IsUnlocked);
    }
    [Fact] public void GeneratorMeetsLengthAndSelectedClasses()
    {
        for (var i = 0; i < 30; i++)
        {
            var p = PasswordGenerator.Generate(); Assert.Equal(24, p.Length);
            Assert.Contains(p, char.IsUpper); Assert.Contains(p, char.IsLower); Assert.Contains(p, char.IsDigit);
            Assert.Contains(p, c => !char.IsLetterOrDigit(c));
        }
        Assert.All(PasswordGenerator.Generate(32, false, false, true, false), c => Assert.True(char.IsDigit(c)));
        Assert.Throws<VaultException>(() => PasswordGenerator.Generate(8));
    }
    [Fact] public void CsvHandlesBomQuotedCommaEscapedQuotesAndMultilineSecrets()
    {
        var path = Path.Combine(_root, "source.csv");
        File.WriteAllText(path, "name,username,password,url,notes\r\n\"Work, app\",user,\" p\"\"ass\nline \",https://example.invalid,note\r\n", new UTF8Encoding(true));
        using var csv = VaultCsv.Read(path); var mapped = csv.Map(0, 1, 2, 3, 4, -1);
        try { Assert.Equal("Work, app", mapped[0].Title); Assert.Equal(" p\"ass\nline ", mapped[0].Password); }
        finally { foreach (var d in mapped) d.Dispose(); }
    }
    [Theory] [InlineData("name,password\nA,\"unterminated")] [InlineData("name,password\nA,one,extra")]
    public void MalformedCsvIsRejected(string content)
    {
        var path = Path.Combine(_root, "bad.csv"); File.WriteAllText(path, content);
        Assert.Throws<VaultException>(() => VaultCsv.Read(path));
    }
    [Theory] [InlineData(2)] [InlineData(4)]
    public void CsvSecretColumnsCannotAlsoBecomeVisibleMetadata(int titleColumn)
    {
        var path = Path.Combine(_root, "mapping.csv");
        File.WriteAllText(path, "name,username,password,url,notes\nExample,user,synthetic-password,https://example.invalid,synthetic-note\n");
        using var csv = VaultCsv.Read(path);
        Assert.Equal(VaultError.InvalidMapping, Assert.Throws<VaultException>(() => csv.Map(titleColumn, 1, 2, 3, 4, -1)).Error);
    }
    public void Dispose()
    {
        foreach (var v in _services) v.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
