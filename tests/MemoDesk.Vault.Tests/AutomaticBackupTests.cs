using MemoDesk.Vault.Core;
using MemoDesk.Vault.Infrastructure;

namespace MemoDesk.Vault.Tests;

public sealed class AutomaticBackupTests : IDisposable
{
    private const string Master = "Synthetic backup master 2026!";
    private const string NewMaster = "Synthetic changed master 2026!";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MemoDesk.Backup.Tests", Guid.NewGuid().ToString("N"));
    private readonly List<VaultService> _services = [];
    private readonly BackupClock _clock = new();

    private VaultService Open(string name)
    {
        var service = new VaultService(Path.Combine(_root, name, "vault.db"), _clock);
        _services.Add(service);
        return service;
    }
    private static void Save(VaultService service, string title)
    {
        using var draft = new VaultEntryDraft { Title = title, Password = "Synthetic backup secret" };
        service.Save(draft);
    }
    private string[] Backups(string name, string prefix)
        => Directory.GetFiles(Path.Combine(_root, name, "backups"), prefix + "-*.memovault");
    private void AssertContents(string path, string password, params string[] titles)
    {
        using var restored = Open("check-" + Guid.NewGuid().ToString("N"));
        restored.RestoreBackup(path, password);
        restored.Unlock(password);
        Assert.Equal(titles.Order(StringComparer.Ordinal), restored.List().Select(r => r.Title).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoredVaultHasIndependentDailyAndWeeklyBackups(bool restartAfterRestore)
    {
        var target = Open("target"); target.Create(Master); Save(target, "Previous vault");
        var previous = Backups("target", "daily").Concat(Backups("target", "weekly")).ToArray();
        // Legacy date-only backups must not suppress the current vault or be deleted by retention.
        var legacyDaily = Path.Combine(_root, "target", "backups", "daily-20261101.memovault");
        var legacyWeekly = Path.Combine(_root, "target", "backups", "weekly-20261026.memovault");
        target.ExportBackup(legacyDaily); target.ExportBackup(legacyWeekly);
        previous = [.. previous, legacyDaily, legacyWeekly];
        var preserved = previous.ToDictionary(p => p, File.ReadAllBytes);

        var source = Open("source"); source.Create(Master); Save(source, "Restored vault");
        var transfer = Path.Combine(_root, "transfer.memovault"); source.ExportBackup(transfer);
        target.RestoreBackup(transfer, Master);
        Assert.False(target.IsUnlocked); Assert.Null(target.LastBackupPath);
        if (restartAfterRestore) { target.Dispose(); target = Open("target"); }
        target.Unlock(Master); Save(target, "Saved after restore");

        foreach (var prefix in new[] { "daily", "weekly" })
        {
            var backup = Assert.Single(Backups("target", prefix).Except(previous));
            AssertContents(backup, Master, "Restored vault", "Saved after restore");
        }
        foreach (var (path, bytes) in preserved) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(target.BackupFailed);
    }

    [Fact]
    public void RestoringSameVaultRefreshesDailyBackupOnNextSave()
    {
        var service = Open("target"); service.Create(Master); Save(service, "First");
        var daily = Assert.Single(Backups("target", "daily"));
        var transfer = Path.Combine(_root, "transfer.memovault"); service.ExportBackup(transfer);
        Save(service, "Discarded by restore");
        service.RestoreBackup(transfer, Master); service.Unlock(Master); Save(service, "After restore");
        AssertContents(daily, Master, "First", "After restore");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPasswordChangeBackupKeepsWarningAndRetriesUntilSuccessful(bool lockBetweenSaves)
    {
        var service = Open("target"); service.Create(Master); Save(service, "First");
        var daily = Assert.Single(Backups("target", "daily"));
        var original = File.ReadAllBytes(daily);
        File.SetAttributes(daily, FileAttributes.ReadOnly);
        try
        {
            service.ChangeMasterPassword(Master, NewMaster);
            Assert.True(service.BackupFailed);
            service.ExportBackup(Path.Combine(_root, "manual.memovault"));
            Assert.True(service.BackupFailed); // A manual export does not repair the automatic destination.
            if (lockBetweenSaves) { service.Lock(); service.Unlock(NewMaster); }
            Save(service, "While destination is blocked");
            Assert.True(service.BackupFailed);
            Assert.Equal(original, File.ReadAllBytes(daily));
            Assert.Equal(2, service.List().Count); // Account changes stay committed despite backup failure.
        }
        finally { File.SetAttributes(daily, FileAttributes.Normal); }

        Save(service, "After repair");
        Assert.False(service.BackupFailed);
        AssertContents(daily, NewMaster, "First", "While destination is blocked", "After repair");
        var next = File.ReadAllBytes(daily);
        Save(service, "Later same day");
        Assert.Equal(next, File.ReadAllBytes(daily)); // Normal once-a-day scheduling resumes after repair.
    }

    [Fact]
    public void InitialBackupFailureDoesNotUndoSaveAndRetriesAfterDirectoryRepair()
    {
        var service = Open("target"); service.Create(Master);
        var directory = Path.Combine(_root, "target", "backups"); File.WriteAllText(directory, "Synthetic obstruction");
        Save(service, "Saved while backup unavailable"); Assert.True(service.BackupFailed);
        Save(service, "Still unavailable"); Assert.True(service.BackupFailed);
        File.Delete(directory); Save(service, "Destination repaired");
        Assert.False(service.BackupFailed);
        foreach (var prefix in new[] { "daily", "weekly" })
            AssertContents(Assert.Single(Backups("target", prefix)), Master,
                "Saved while backup unavailable", "Still unavailable", "Destination repaired");
    }

    [Fact]
    public void WeeklyFailureKeepsWarningUntilBothSchedulesSucceed()
    {
        var service = Open("target"); service.Create(Master); Save(service, "First");
        var weekly = Assert.Single(Backups("target", "weekly"));
        File.Delete(weekly); Directory.CreateDirectory(weekly);
        service.ChangeMasterPassword(Master, NewMaster); // Daily write succeeds before weekly fails.
        Assert.True(service.BackupFailed);
        Save(service, "While weekly unavailable"); Assert.True(service.BackupFailed);
        Directory.Delete(weekly); Save(service, "Weekly repaired");
        Assert.False(service.BackupFailed);
        foreach (var prefix in new[] { "daily", "weekly" })
            AssertContents(Assert.Single(Backups("target", prefix)), NewMaster,
                "First", "While weekly unavailable", "Weekly repaired");
    }

    [Fact]
    public void RetentionIsScopedToVaultIdentity()
    {
        var target = Open("target"); target.Create(Master); Save(target, "Previous vault");
        var previous = Backups("target", "daily").Concat(Backups("target", "weekly")).ToDictionary(p => p, File.ReadAllBytes);
        var source = Open("source"); source.Create(Master); Save(source, "Restored vault");
        var transfer = Path.Combine(_root, "transfer.memovault"); source.ExportBackup(transfer);
        target.RestoreBackup(transfer, Master); target.Unlock(Master);
        for (var i = 0; i < 9; i++) { _clock.Now = _clock.Now.AddDays(7); Save(target, "Week " + i); }
        Assert.Equal(7, Backups("target", "daily").Except(previous.Keys).Count());
        Assert.Equal(4, Backups("target", "weekly").Except(previous.Keys).Count());
        foreach (var (path, bytes) in previous) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private sealed class BackupClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
