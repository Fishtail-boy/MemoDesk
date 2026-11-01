using System.Security.Cryptography;
using System.Text.Json;
using MemoDesk.Vault.Core;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Vault.Infrastructure;

/// <summary>独立的本地密码库。所有写入与锁定串行化，宿主只获得固定错误码。</summary>
public sealed partial class VaultService : IVaultService
{
    private readonly object _gate = new();
    private readonly VaultStore _store;
    private readonly TimeProvider _clock;
    private VaultEnvelope? _envelope;
    private byte[]? _key;
    private long _generation;
    private long _keyGeneration;
    private bool _disposed;
    private bool _unlocked;
    public VaultService(string databasePath, TimeProvider? clock = null)
    {
        _store = new VaultStore(databasePath);
        _clock = clock ?? TimeProvider.System;
    }
    public bool Exists => _store.Exists;
    public bool IsUnlocked => Volatile.Read(ref _unlocked) && Interlocked.Read(ref _keyGeneration) == SessionVersion && !Volatile.Read(ref _disposed);
    public long SessionVersion => Interlocked.Read(ref _generation);
    public event Action? Locked;
    public string? LastBackupPath { get; private set; }
    public bool BackupFailed { get; private set; }

    public void Create(string masterPassword, long? expectedSessionVersion = null)
    {
        var generation = expectedSessionVersion ?? SessionVersion;
        Run(() =>
        {
            CheckGeneration(generation);
            if (Exists) throw new VaultException(VaultError.AlreadyExists);
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                var envelope = VaultCrypto.Wrap(Guid.NewGuid(), 1, key, masterPassword);
                CheckGeneration(generation);
                _store.Initialize(envelope);
                CheckGeneration(generation);
                ClearKey(); _key = key; key = []; _envelope = envelope; _keyGeneration = generation; Volatile.Write(ref _unlocked, true);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        });
    }
    public void Unlock(string masterPassword, long? expectedSessionVersion = null)
    {
        var generation = expectedSessionVersion ?? SessionVersion;
        Run(() =>
        {
            CheckGeneration(generation);
            ClearKey();
            var envelope = _store.ReadEnvelope();
            var key = VaultCrypto.Unwrap(envelope, masterPassword);
            try
            {
                ValidateRecords(_store, envelope, key);
                CheckGeneration(generation);
                _key = key; key = []; _envelope = envelope; _keyGeneration = generation; Volatile.Write(ref _unlocked, true);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        });
    }
    public void Lock()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _unlocked, false);
        // Revoke access immediately; a running operation keeps its buffers only until it ends.
        if (Monitor.TryEnter(_gate))
        {
            try { ClearKey(); } finally { Monitor.Exit(_gate); }
        }
        else
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (_gate)
                {
                    // Preserve a later unlock that belongs to the current generation.
                    if (_keyGeneration != SessionVersion || Volatile.Read(ref _disposed)) ClearKey();
                }
            });
        }
        Locked?.Invoke();
    }
    private void ClearKey()
    {
        Volatile.Write(ref _unlocked, false);
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _key = null; _envelope = null;
    }
    private void CheckGeneration(long generation)
    {
        if (generation != SessionVersion) throw new VaultException(VaultError.Cancelled);
    }
    private void RequireUnlocked()
    {
        if (!IsUnlocked || _key is null || _envelope is null) throw new VaultException(VaultError.Locked);
    }
    public IReadOnlyList<VaultEntrySummary> List() => Run(() =>
    {
        RequireUnlocked();
        return (IReadOnlyList<VaultEntrySummary>)_store.ReadAll().Select(ReadMetadata).ToArray();
    });
    private VaultEntrySummary ReadMetadata(StoredEntry row)
        => ReadMetadata(row, _envelope!, _key!);
    private static VaultEntrySummary ReadMetadata(StoredEntry row, VaultEnvelope envelope, byte[] key)
    {
        var meta = VaultCrypto.Decrypt<VaultEntrySummary>(row.Metadata, key, VaultCrypto.EntryAad(envelope, row.Id, row.Version, false));
        if (meta.Id != row.Id || meta.RowVersion != row.Version || string.IsNullOrWhiteSpace(meta.Title) ||
            new[] { meta.Title, meta.UserName, meta.Url, meta.Category, meta.Tags }.Any(s => s is null || s.Length > 2048))
            throw new VaultException(VaultError.InvalidData);
        return meta;
    }
    private static SecretData ReadSecretData(StoredEntry row, VaultEnvelope envelope, byte[] key)
    {
        var secret = VaultCrypto.Decrypt<SecretData>(row.Secret, key, VaultCrypto.EntryAad(envelope, row.Id, row.Version, true));
        if (secret.Password is null || secret.Notes is null || secret.Password.Length > 65536 || secret.Notes.Length > 65536)
            throw new VaultException(VaultError.InvalidData);
        return secret;
    }
    private static void ValidateRecords(VaultStore store, VaultEnvelope envelope, byte[] key)
    {
        foreach (var row in store.ReadAll())
        {
            ReadMetadata(row, envelope, key);
            var secret = ReadSecretData(row, envelope, key);
            secret.Password = secret.Notes = string.Empty;
        }
    }
    public VaultSecret ReadSecret(Guid id, long expectedVersion) => Run(() =>
    {
        RequireUnlocked();
        var row = _store.Read(id);
        if (row.Version != expectedVersion) throw new VaultException(VaultError.Conflict);
        if (ReadMetadata(row).IsDeleted) throw new VaultException(VaultError.NotFound);
        var data = ReadSecretData(row, _envelope!, _key!);
        try { return new VaultSecret(data.Password, data.Notes); }
        finally { data.Password = data.Notes = string.Empty; }
    });
    public void Save(VaultEntryDraft draft) => Run(() =>
    {
        RequireUnlocked(); draft.Validate();
        var old = draft.ExpectedVersion == 0 ? null : ReadMetadata(_store.Read(draft.Id));
        if (old?.IsDeleted == true) throw new VaultException(VaultError.Conflict);
        using var c = _store.Open(); using var tx = c.BeginTransaction();
        SaveDraft(c, tx, draft, old);
        CheckGeneration(_keyGeneration); tx.Commit();
        AutomaticBackup();
    });
    private void SaveDraft(SqliteConnection c, SqliteTransaction tx, VaultEntryDraft draft, VaultEntrySummary? old, bool deleted = false)
    {
        var now = _clock.GetUtcNow();
        _store.Write(c, tx, draft.Id, draft.ExpectedVersion, version =>
        {
            var meta = new VaultEntrySummary(draft.Id, version, draft.Title, draft.UserName, draft.Url,
                string.IsNullOrWhiteSpace(draft.Category) ? "个人" : draft.Category, draft.Tags,
                draft.Favorite, deleted, old?.CreatedAt ?? now, now);
            return (VaultCrypto.Encrypt(meta, _key!, VaultCrypto.EntryAad(_envelope!, draft.Id, version, false)),
                VaultCrypto.Encrypt(new SecretData { Password = draft.Password, Notes = draft.Notes },
                    _key!, VaultCrypto.EntryAad(_envelope!, draft.Id, version, true)));
        });
    }
    public void SetDeleted(Guid id, long expectedVersion, bool deleted) => Run(() =>
    {
        RequireUnlocked();
        var row = _store.Read(id); var meta = ReadMetadata(row);
        if (row.Version != expectedVersion) throw new VaultException(VaultError.Conflict);
        var secret = ReadSecretData(row, _envelope!, _key!);
        using var draft = new VaultEntryDraft
        {
            Id = id, ExpectedVersion = expectedVersion, Title = meta.Title, UserName = meta.UserName,
            Url = meta.Url, Category = meta.Category, Tags = meta.Tags, Favorite = meta.Favorite,
            Password = secret.Password, Notes = secret.Notes
        };
        secret.Password = secret.Notes = string.Empty;
        using var c = _store.Open(); using var tx = c.BeginTransaction();
        SaveDraft(c, tx, draft, meta, deleted);
        CheckGeneration(_keyGeneration); tx.Commit(); AutomaticBackup();
    });
    public void DeletePermanently(Guid id, long expectedVersion) => Run(() =>
    {
        RequireUnlocked();
        var meta = ReadMetadata(_store.Read(id));
        if (!meta.IsDeleted || meta.RowVersion != expectedVersion) throw new VaultException(VaultError.Conflict);
        using var c = _store.Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM vault_entries WHERE id=$id AND row_version=$version";
        cmd.Parameters.AddWithValue("$id", id.ToString("N")); cmd.Parameters.AddWithValue("$version", expectedVersion);
        if (cmd.ExecuteNonQuery() != 1) throw new VaultException(VaultError.Conflict);
        CheckGeneration(_keyGeneration); tx.Commit(); AutomaticBackup();
    });
    public int Import(IReadOnlyList<VaultEntryDraft> entries, Guid batchId) => Run(() =>
    {
        RequireUnlocked();
        if (batchId == Guid.Empty || entries.Count > VaultStore.MaxEntries) throw new VaultException(VaultError.TooLarge);
        foreach (var entry in entries) { entry.Validate(); if (entry.ExpectedVersion != 0) throw new VaultException(VaultError.InvalidData); }
        using var c = _store.Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO import_batches(id) VALUES($id)";
        cmd.Parameters.AddWithValue("$id", batchId.ToString("N"));
        if (cmd.ExecuteNonQuery() == 0) return 0;
        foreach (var entry in entries) SaveDraft(c, tx, entry, null);
        CheckGeneration(_keyGeneration); tx.Commit(); AutomaticBackup(); return entries.Count;
    });
    public void ChangeMasterPassword(string currentPassword, string newPassword) => Run(() =>
    {
        RequireUnlocked();
        var envelope = _store.ReadEnvelope();
        var key = VaultCrypto.Unwrap(envelope, currentPassword);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(key, _key!)) throw new VaultException(VaultError.Conflict);
            var next = VaultCrypto.Wrap(envelope.VaultId, envelope.KeyVersion, key, newPassword);
            var verified = VaultCrypto.Unwrap(next, newPassword);
            CryptographicOperations.ZeroMemory(verified);
            using var c = _store.Open(); using var tx = c.BeginTransaction();
            _store.WriteEnvelope(c, tx, next); CheckGeneration(_keyGeneration); tx.Commit(); _envelope = next;
            AutomaticBackup(force: true);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    });
    private void Run(Action operation) => Run(() => { operation(); return true; });
    private T Run<T>(Func<T> operation)
    {
        lock (_gate)
        {
            if (_disposed) throw new VaultException(VaultError.Locked);
            try { return operation(); }
            catch (VaultException) { throw; }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            { throw new VaultException(VaultError.Storage); }
            catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException or
                FormatException or OverflowException or InvalidOperationException)
            { throw new VaultException(VaultError.InvalidData); }
            finally { if (_key is not null && _keyGeneration != SessionVersion) ClearKey(); }
        }
    }
    public void Dispose()
    {
        Lock(); Volatile.Write(ref _disposed, true);
        GC.SuppressFinalize(this);
    }
}
