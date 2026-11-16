using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalVault.Models;

namespace LocalVault.Services;

/// <summary>
/// Owns the unlocked session: the derived key lives only in memory (and is zeroed on lock),
/// and decrypted entries are cached only while unlocked.
/// </summary>
public sealed class VaultService
{
    public const int MinimumMasterPasswordLength = 8;

    private readonly VaultStore _store;
    private readonly int _newVaultIterations;
    private byte[]? _key;
    private readonly List<VaultEntry> _entries = [];

    public event EventHandler? Locked;
    public event EventHandler? Unlocked;
    public event EventHandler? EntriesChanged;

    public VaultService(VaultStore store, int newVaultIterations = VaultCryptoService.DefaultIterations)
    {
        _store = store;
        _newVaultIterations = newVaultIterations;
    }

    public bool IsInitialized => _store.IsInitialized;
    public bool IsUnlocked => _key is not null;
    public string DatabasePath => _store.DatabasePath;

    public IReadOnlyList<VaultEntry> Entries => IsUnlocked ? _entries : [];

    public void CreateVault(string masterPassword)
    {
        if (IsInitialized) throw new InvalidOperationException("A vault already exists.");
        if (string.IsNullOrEmpty(masterPassword) || masterPassword.Length < MinimumMasterPasswordLength)
            throw new ArgumentException($"Master password must be at least {MinimumMasterPasswordLength} characters.");

        CreateVaultCore(masterPassword);
        CompleteUnlock();
    }

    /// <summary>Derives the key and checks it against the stored verifier. Returns false on a wrong password.</summary>
    public bool TryUnlock(string masterPassword)
    {
        if (IsUnlocked) return true;
        if (!TryUnlockCore(masterPassword)) return false;
        CompleteUnlock();
        return true;
    }

    // The *Core methods do the slow KDF work and may run on a worker thread; CompleteUnlock()
    // must then be called on the UI thread to publish the unlocked state and raise events.
    private byte[]? _pendingKey;
    private List<VaultEntry>? _pendingEntries;

    public void CreateVaultCore(string masterPassword)
    {
        if (IsInitialized) throw new InvalidOperationException("A vault already exists.");
        if (string.IsNullOrEmpty(masterPassword) || masterPassword.Length < MinimumMasterPasswordLength)
            throw new ArgumentException($"Master password must be at least {MinimumMasterPasswordLength} characters.");

        byte[] salt = VaultCryptoService.GenerateSalt();
        byte[] key = VaultCryptoService.DeriveKey(masterPassword, salt, _newVaultIterations);
        _store.Initialize(new KdfParameters(salt, _newVaultIterations, VaultCryptoService.CreateKeyVerifier(key)));
        _pendingKey = key;
        _pendingEntries = [];
    }

    public bool TryUnlockCore(string masterPassword)
    {
        var kdf = _store.ReadKdfParameters();
        byte[] key = VaultCryptoService.DeriveKey(masterPassword ?? "", kdf.Salt, kdf.Iterations);
        if (!VaultCryptoService.VerifyKey(key, kdf.Verifier))
        {
            CryptographicOperations.ZeroMemory(key);
            return false;
        }

        var loaded = new List<VaultEntry>();
        foreach (var record in _store.ReadAll())
        {
            string json = VaultCryptoService.DecryptString(key, record.Blob, Aad(record.Id));
            var entry = JsonSerializer.Deserialize<VaultEntry>(json);
            if (entry is not null)
            {
                entry.Id = record.Id; // the AAD binding guarantees this matches
                loaded.Add(entry);
            }
        }

        _pendingKey = key;
        _pendingEntries = loaded;
        return true;
    }

    public void CompleteUnlock()
    {
        if (_pendingKey is null || _pendingEntries is null) return;
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _key = _pendingKey;
        _entries.Clear();
        _entries.AddRange(_pendingEntries);
        _pendingKey = null;
        _pendingEntries = null;
        Unlocked?.Invoke(this, EventArgs.Empty);
        EntriesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Lock()
    {
        if (_key is null) return;
        CryptographicOperations.ZeroMemory(_key);
        _key = null;
        _entries.Clear();
        Locked?.Invoke(this, EventArgs.Empty);
        EntriesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Inserts or updates an entry (matched by Id).</summary>
    public VaultEntry Save(VaultEntry entry)
    {
        byte[] key = RequireKey();
        var copy = entry.Clone();
        copy.Modified = DateTimeOffset.Now;
        string json = JsonSerializer.Serialize(copy);
        byte[] blob = VaultCryptoService.EncryptString(key, json, Aad(copy.Id));
        _store.Upsert(new StoredRecord(copy.Id, blob));

        int index = _entries.FindIndex(e => e.Id == copy.Id);
        if (index >= 0) _entries[index] = copy; else _entries.Insert(0, copy);
        EntriesChanged?.Invoke(this, EventArgs.Empty);
        return copy;
    }

    public void Delete(Guid id)
    {
        RequireKey();
        _store.Delete(id);
        _entries.RemoveAll(e => e.Id == id);
        EntriesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Finds an existing entry that matches a captured site/app and username.</summary>
    public VaultEntry? FindMatch(string? host, string? processName, string? username)
    {
        if (!IsUnlocked) return null;
        username ??= "";
        foreach (var e in _entries)
        {
            bool sameTarget = !string.IsNullOrEmpty(host)
                ? string.Equals(VaultEntry.TryGetHost(e.Url), host, StringComparison.OrdinalIgnoreCase)
                : !string.IsNullOrEmpty(processName) && string.Equals(e.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
            if (sameTarget && string.Equals(e.Username, username, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }

    private byte[] RequireKey() => _key ?? throw new InvalidOperationException("The vault is locked.");

    private static byte[] Aad(Guid id) => Encoding.UTF8.GetBytes("LocalVault/entry/" + id.ToString("D"));
}
