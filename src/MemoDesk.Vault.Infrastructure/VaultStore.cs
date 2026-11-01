using System.Text.Json;
using MemoDesk.Vault.Core;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Vault.Infrastructure;

internal sealed class VaultStore(string path)
{
    public const int MaxEntries = 10000;
    private const long MaxDatabaseBytes = 64 * 1024 * 1024;
    private static readonly string[] Schema =
    [
        "CREATE TABLE vault_meta (id INTEGER PRIMARY KEY CHECK(id=1), envelope TEXT NOT NULL)",
        "CREATE TABLE vault_entries (id TEXT PRIMARY KEY, row_version INTEGER NOT NULL, metadata BLOB NOT NULL, secret BLOB NOT NULL)",
        "CREATE TABLE used_nonces (nonce BLOB PRIMARY KEY)",
        "CREATE TABLE import_batches (id TEXT PRIMARY KEY)"
    ];
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public bool Exists => File.Exists(Path);

    public SqliteConnection Open(bool create = false, bool readOnly = false)
    {
        if (Exists && new FileInfo(Path).Length > MaxDatabaseBytes) throw new VaultException(VaultError.TooLarge);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path, Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite
        }.ToString());
        try
        {
            c.Open();
            using var command = c.CreateCommand();
            command.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA busy_timeout=3000;" +
                (readOnly ? "" : "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA secure_delete=ON; PRAGMA max_page_count=16384;");
            command.ExecuteNonQuery();
            return c;
        }
        catch { c.Dispose(); throw; }
    }
    public void Initialize(VaultEnvelope envelope)
    {
        if (Exists) throw new VaultException(VaultError.AlreadyExists);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".create-" + Guid.NewGuid().ToString("N");
        try
        {
            var store = new VaultStore(temp);
            using (var c = store.Open(create: true))
            {
                using var tx = c.BeginTransaction();
                using var cmd = c.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = string.Join(';', Schema) + "; PRAGMA user_version=1;";
                cmd.ExecuteNonQuery();
                store.WriteEnvelope(c, tx, envelope);
                tx.Commit();
            }
            File.Move(temp, Path, overwrite: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public VaultEnvelope ReadEnvelope()
    {
        using var c = Open(readOnly: true);
        using (var schema = c.CreateCommand())
        {
            schema.CommandText = "SELECT type,sql FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*'";
            using var rows = schema.ExecuteReader();
            var expected = Schema.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
            while (rows.Read())
            {
                if (rows.GetString(0) != "table" || rows.IsDBNull(1) || !expected.Remove(Normalize(rows.GetString(1))))
                    throw new VaultException(VaultError.InvalidData);
            }
            if (expected.Count != 0) throw new VaultException(VaultError.InvalidData);
        }
        using var v = c.CreateCommand(); v.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(v.ExecuteScalar()) != 1) throw new VaultException(VaultError.InvalidData);
        v.CommandText = "SELECT CASE WHEN length(envelope)<=8192 THEN envelope END FROM vault_meta WHERE id=1";
        var value = v.ExecuteScalar() as string;
        if (value is null || value.Length > 8192) throw new VaultException(VaultError.InvalidData);
        return JsonSerializer.Deserialize<VaultEnvelope>(value) ?? throw new VaultException(VaultError.InvalidData);
        static string Normalize(string sql) => string.Concat(sql.Where(c => !char.IsWhiteSpace(c))).TrimEnd(';');
    }
    public void WriteEnvelope(SqliteConnection c, SqliteTransaction tx, VaultEnvelope e)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO vault_meta(id,envelope) VALUES(1,$value) ON CONFLICT(id) DO UPDATE SET envelope=excluded.envelope";
        cmd.Parameters.AddWithValue("$value", JsonSerializer.Serialize(e)); cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<StoredEntry> ReadAll()
    {
        using var c = Open(readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,row_version,metadata,secret FROM vault_entries LIMIT 10001";
        using var reader = cmd.ExecuteReader();
        var rows = new List<StoredEntry>();
        long totalBytes = 0;
        while (reader.Read())
        {
            totalBytes += reader.GetBytes(2, 0, null, 0, 0) + reader.GetBytes(3, 0, null, 0, 0);
            if (totalBytes > MaxDatabaseBytes) throw new VaultException(VaultError.TooLarge);
            if (rows.Count >= MaxEntries || reader.GetBytes(2, 0, null, 0, 0) > VaultCrypto.MaxPayload + 29 ||
                reader.GetBytes(3, 0, null, 0, 0) > VaultCrypto.MaxPayload + 29) throw new VaultException(VaultError.TooLarge);
            if (!Guid.TryParseExact(reader.GetString(0), "N", out var id) || id == Guid.Empty || reader.GetInt64(1) <= 0)
                throw new VaultException(VaultError.InvalidData);
            rows.Add(new StoredEntry(id, reader.GetInt64(1), (byte[])reader[2], (byte[])reader[3]));
        }
        return rows;
    }
    public StoredEntry Read(Guid id) => ReadAll().SingleOrDefault(r => r.Id == id) ?? throw new VaultException(VaultError.NotFound);

    public void Write(SqliteConnection c, SqliteTransaction tx, Guid id, long expectedVersion,
        Func<long, (byte[] Metadata, byte[] Secret)> encrypt)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT row_version FROM vault_entries WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString("N"));
        var current = cmd.ExecuteScalar();
        if ((current is null && expectedVersion != 0) || (current is not null && Convert.ToInt64(current) != expectedVersion))
            throw new VaultException(VaultError.Conflict);
        if (current is null)
        {
            cmd.CommandText = "SELECT count(*) FROM vault_entries";
            if (Convert.ToInt64(cmd.ExecuteScalar()) >= MaxEntries) throw new VaultException(VaultError.TooLarge);
        }
        var next = checked(expectedVersion + 1);
        (byte[] Metadata, byte[] Secret) data = default;
        var registered = false;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            data = encrypt(next);
            if (Register(data.Metadata) && Register(data.Secret)) { registered = true; break; }
        }
        if (!registered) throw new VaultException(VaultError.Conflict);
        cmd.CommandText = """
            INSERT INTO vault_entries(id,row_version,metadata,secret) VALUES($id,$version,$meta,$secret)
            ON CONFLICT(id) DO UPDATE SET row_version=excluded.row_version,metadata=excluded.metadata,secret=excluded.secret
            """;
        cmd.Parameters.AddWithValue("$version", next);
        cmd.Parameters.AddWithValue("$meta", data.Metadata); cmd.Parameters.AddWithValue("$secret", data.Secret);
        cmd.ExecuteNonQuery();
        bool Register(byte[] payload)
        {
            using var n = c.CreateCommand(); n.Transaction = tx;
            n.CommandText = "INSERT OR IGNORE INTO used_nonces(nonce) VALUES($nonce)";
            n.Parameters.AddWithValue("$nonce", payload.AsSpan(1, 12).ToArray());
            return n.ExecuteNonQuery() == 1;
        }
    }
    public void Snapshot(string target)
    {
        using var source = Open(readOnly: true);
        using var destination = new VaultStore(target).Open(create: true);
        source.BackupDatabase(destination);
    }
}
