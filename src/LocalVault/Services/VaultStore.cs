using Microsoft.Data.Sqlite;

namespace LocalVault.Services;

public sealed record KdfParameters(byte[] Salt, int Iterations, byte[] Verifier);

public sealed record StoredRecord(Guid Id, byte[] Blob);

/// <summary>
/// SQLite persistence. Stores only ciphertext — every credential row is a single opaque
/// AES-GCM blob (nonce || tag || ciphertext). No plaintext metadata is written to disk.
/// </summary>
public sealed class VaultStore
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public VaultStore(string databasePath)
    {
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        EnsureSchema();
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "vault.db");

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS entries (
                id       TEXT PRIMARY KEY,
                blob     BLOB NOT NULL,
                updated  INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public bool IsInitialized => ReadMeta("salt") is not null;

    public void Initialize(KdfParameters kdf)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        WriteMeta(conn, tx, "salt", kdf.Salt);
        WriteMeta(conn, tx, "iterations", BitConverter.GetBytes(kdf.Iterations));
        WriteMeta(conn, tx, "verifier", kdf.Verifier);
        WriteMeta(conn, tx, "kdf", "pbkdf2-sha512"u8.ToArray());
        WriteMeta(conn, tx, "cipher", "aes-256-gcm"u8.ToArray());
        tx.Commit();
    }

    public KdfParameters ReadKdfParameters()
    {
        byte[] salt = ReadMeta("salt") ?? throw new InvalidOperationException("Vault is not initialized.");
        byte[] iter = ReadMeta("iterations") ?? throw new InvalidOperationException("Vault metadata missing.");
        byte[] verifier = ReadMeta("verifier") ?? throw new InvalidOperationException("Vault metadata missing.");
        return new KdfParameters(salt, BitConverter.ToInt32(iter), verifier);
    }

    public IReadOnlyList<StoredRecord> ReadAll()
    {
        var list = new List<StoredRecord>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, blob FROM entries ORDER BY updated DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new StoredRecord(Guid.Parse(reader.GetString(0)), (byte[])reader.GetValue(1)));
        return list;
    }

    public void Upsert(StoredRecord record)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entries (id, blob, updated) VALUES ($id, $blob, $updated)
            ON CONFLICT(id) DO UPDATE SET blob = excluded.blob, updated = excluded.updated
            """;
        cmd.Parameters.AddWithValue("$id", record.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$blob", record.Blob);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public void Delete(Guid id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM entries WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.ExecuteNonQuery();
    }

    private byte[]? ReadMeta(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as byte[];
    }

    private static void WriteMeta(SqliteConnection conn, SqliteTransaction tx, string key, byte[] value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value)";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }
}
