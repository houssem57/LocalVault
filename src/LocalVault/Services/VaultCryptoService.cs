using System.Security.Cryptography;
using System.Text;

namespace LocalVault.Services;

/// <summary>An AES-256-GCM ciphertext together with its nonce and authentication tag.</summary>
public sealed record EncryptedBlob(byte[] Nonce, byte[] Ciphertext, byte[] Tag)
{
    /// <summary>Serializes as nonce || tag || ciphertext.</summary>
    public byte[] ToBytes()
    {
        var result = new byte[Nonce.Length + Tag.Length + Ciphertext.Length];
        Nonce.CopyTo(result, 0);
        Tag.CopyTo(result, Nonce.Length);
        Ciphertext.CopyTo(result, Nonce.Length + Tag.Length);
        return result;
    }

    public static EncryptedBlob FromBytes(byte[] data)
    {
        const int header = VaultCryptoService.NonceSize + VaultCryptoService.TagSize;
        if (data.Length < header)
            throw new CryptographicException("Encrypted blob is truncated.");
        return new EncryptedBlob(
            data[..VaultCryptoService.NonceSize],
            data[header..],
            data[VaultCryptoService.NonceSize..header]);
    }
}

/// <summary>
/// Key derivation (PBKDF2-HMAC-SHA512) and authenticated encryption (AES-256-GCM) primitives.
/// Stateless and thread-safe.
/// </summary>
public static class VaultCryptoService
{
    public const int KeySize = 32;          // 256-bit key
    public const int SaltSize = 32;         // 256-bit salt
    public const int NonceSize = 12;        // 96-bit GCM nonce
    public const int TagSize = 16;          // 128-bit GCM tag
    public const int DefaultIterations = 600_000;

    private static readonly byte[] VerifierPlaintext = Encoding.UTF8.GetBytes("LocalVault/key-verifier/v1");
    private static readonly byte[] VerifierAad = Encoding.UTF8.GetBytes("LocalVault/verifier");

    public static byte[] GenerateSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    public static byte[] DeriveKey(string masterPassword, byte[] salt, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(masterPassword);
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length < 16) throw new ArgumentException("Salt too short.", nameof(salt));
        if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));

        byte[] passwordBytes = Encoding.UTF8.GetBytes(masterPassword);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA512, KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>Encrypts with a fresh random nonce. <paramref name="associatedData"/> is authenticated but not encrypted.</summary>
    public static EncryptedBlob Encrypt(byte[] key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        ValidateKey(key);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return new EncryptedBlob(nonce, ciphertext, tag);
    }

    /// <summary>Decrypts and authenticates. Throws <see cref="CryptographicException"/> on a wrong key or tampering.</summary>
    public static byte[] Decrypt(byte[] key, EncryptedBlob blob, ReadOnlySpan<byte> associatedData = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Nonce.Length != NonceSize || blob.Tag.Length != TagSize)
            throw new CryptographicException("Invalid nonce or tag length.");
        byte[] plaintext = new byte[blob.Ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(blob.Nonce, blob.Ciphertext, blob.Tag, plaintext, associatedData);
        return plaintext;
    }

    public static byte[] EncryptString(byte[] key, string plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        byte[] data = Encoding.UTF8.GetBytes(plaintext);
        try { return Encrypt(key, data, associatedData).ToBytes(); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    public static string DecryptString(byte[] key, byte[] blob, ReadOnlySpan<byte> associatedData = default)
    {
        byte[] data = Decrypt(key, EncryptedBlob.FromBytes(blob), associatedData);
        try { return Encoding.UTF8.GetString(data); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    /// <summary>Creates a blob that proves knowledge of the key without storing anything derived from it directly.</summary>
    public static byte[] CreateKeyVerifier(byte[] key) => Encrypt(key, VerifierPlaintext, VerifierAad).ToBytes();

    /// <summary>Returns true only if <paramref name="key"/> is the key that produced <paramref name="verifier"/>.</summary>
    public static bool VerifyKey(byte[] key, byte[] verifier)
    {
        try
        {
            byte[] plain = Decrypt(key, EncryptedBlob.FromBytes(verifier), VerifierAad);
            return CryptographicOperations.FixedTimeEquals(plain, VerifierPlaintext);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void ValidateKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySize) throw new ArgumentException($"Key must be {KeySize} bytes.", nameof(key));
    }
}
