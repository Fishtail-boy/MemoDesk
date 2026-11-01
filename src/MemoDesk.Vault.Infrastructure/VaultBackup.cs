using System.Buffers.Binary;
using System.Security.Cryptography;
using MemoDesk.Vault.Core;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Vault.Infrastructure;

public sealed partial class VaultService
{
    private const int MaxSnapshot = 64 * 1024 * 1024;
    private static ReadOnlySpan<byte> BackupMagic => "MEMOVLT1"u8;
    private string BackupDirectory => Path.Combine(Path.GetDirectoryName(_store.Path)!, "backups");
    private bool _refreshAutomaticBackup;

    public void ExportBackup(string path) => Run(() => { RequireUnlocked(); WriteBackup(path); });
    private void WriteBackup(string path)
    {
        var target = Path.GetFullPath(path);
        if (string.Equals(target, _store.Path, StringComparison.OrdinalIgnoreCase)) throw new VaultException(VaultError.InvalidData);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var snapshot = _store.Path + ".snapshot-" + Guid.NewGuid().ToString("N");
        var temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        byte[]? authKey = null;
        try
        {
            _store.Snapshot(snapshot);
            var size = new FileInfo(snapshot).Length;
            if (size is < 100 or > MaxSnapshot) throw new VaultException(VaultError.TooLarge);
            var data = new byte[checked(16 + (int)size)];
            BackupMagic.CopyTo(data); BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8, 8), size);
            using (var input = File.OpenRead(snapshot)) input.ReadExactly(data.AsSpan(16));
            authKey = VaultCrypto.BackupKey(_key!, _envelope!.VaultId);
            var tag = HMACSHA256.HashData(authKey, data);
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(data); output.Write(tag); output.Flush(flushToDisk: true); }
            CheckGeneration(_keyGeneration);
            File.Move(temp, target, overwrite: true);
            LastBackupPath = target;
        }
        finally
        {
            if (authKey is not null) CryptographicOperations.ZeroMemory(authKey);
            if (File.Exists(snapshot)) File.Delete(snapshot);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
    private void AutomaticBackup(bool force = false)
    {
        _refreshAutomaticBackup |= force;
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            var date = _clock.GetLocalNow().Date;
            // Date-only files may belong to a different vault restored into this directory.
            var vaultId = _envelope!.VaultId.ToString("N");
            var daily = Path.Combine(BackupDirectory, $"daily-{vaultId}-{date:yyyyMMdd}.memovault");
            if (_refreshAutomaticBackup || !File.Exists(daily)) WriteBackup(daily);
            var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
            var weekly = Path.Combine(BackupDirectory, $"weekly-{vaultId}-{monday:yyyyMMdd}.memovault");
            if (!File.Exists(weekly)) WriteBackup(weekly);
            Retain($"daily-{vaultId}-*.memovault", 7); Retain($"weekly-{vaultId}-*.memovault", 4);
            _refreshAutomaticBackup = false;
            BackupFailed = false;
        }
        catch (Exception ex) when (ex is VaultException or IOException or UnauthorizedAccessException or SqliteException)
        { _refreshAutomaticBackup = true; BackupFailed = true; }
        void Retain(string pattern, int count)
        {
            foreach (var file in Directory.EnumerateFiles(BackupDirectory, pattern)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(count)) File.Delete(file);
        }
    }
    public void RestoreBackup(string path, string masterPassword, long? expectedSessionVersion = null)
    {
        var generation = expectedSessionVersion ?? SessionVersion;
        Run(() =>
        {
            CheckGeneration(generation);
            Directory.CreateDirectory(Path.GetDirectoryName(_store.Path)!);
            var staging = _store.Path + ".restore-" + Guid.NewGuid().ToString("N");
            byte[]? key = null;
            byte[]? authKey = null;
            try
            {
                using (var input = File.OpenRead(path))
                {
                    if (input.Length is < 148 or > MaxSnapshot + 48) throw new VaultException(VaultError.TooLarge);
                    var data = new byte[checked((int)input.Length)]; input.ReadExactly(data);
                    var size = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8, 8));
                    if (!data.AsSpan(0, 8).SequenceEqual(BackupMagic) || size != data.Length - 48 ||
                        !data.AsSpan(16, 16).SequenceEqual("SQLite format 3\0"u8)) throw new VaultException(VaultError.InvalidData);
                    using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { output.Write(data.AsSpan(16, (int)size)); output.Flush(flushToDisk: true); }
                    var candidate = new VaultStore(staging);
                    var envelope = candidate.ReadEnvelope(); key = VaultCrypto.Unwrap(envelope, masterPassword);
                    authKey = VaultCrypto.BackupKey(key, envelope.VaultId);
                    var tag = HMACSHA256.HashData(authKey, data.AsSpan(0, data.Length - 32));
                    if (!CryptographicOperations.FixedTimeEquals(tag, data.AsSpan(data.Length - 32))) throw new VaultException(VaultError.InvalidData);
                    ValidateRecords(candidate, envelope, key);
                    using var c = candidate.Open(readOnly: true); using var check = c.CreateCommand();
                    check.CommandText = "PRAGMA quick_check";
                    if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal)) throw new VaultException(VaultError.InvalidData);
                }
                CheckGeneration(generation);
                // DELETE journal + no pooling + gate: all owned connections are closed before atomic replacement.
                if (Exists) File.Replace(staging, _store.Path, _store.Path + ".before-restore-" + Guid.NewGuid().ToString("N") + ".db");
                else File.Move(staging, _store.Path);
                ClearKey(); Interlocked.Increment(ref _generation);
                _refreshAutomaticBackup = true;
                LastBackupPath = null; BackupFailed = false;
            }
            finally
            {
                if (key is not null) CryptographicOperations.ZeroMemory(key);
                if (authKey is not null) CryptographicOperations.ZeroMemory(authKey);
                if (File.Exists(staging)) File.Delete(staging);
            }
        });
        Locked?.Invoke();
    }
}
