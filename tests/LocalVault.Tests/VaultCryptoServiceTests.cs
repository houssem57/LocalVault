using System.Security.Cryptography;
using System.Text;
using LocalVault.Services;

namespace LocalVault.Tests;

public class VaultCryptoServiceTests
{
    // Lower iteration count keeps the suite fast; the production default is exercised separately.
    private const int TestIterations = 10_000;

    [Fact]
    public void Encrypt_Decrypt_RoundTrip_ReturnsOriginalPlaintext()
    {
        byte[] salt = VaultCryptoService.GenerateSalt();
        byte[] key = VaultCryptoService.DeriveKey("correct horse battery staple", salt, TestIterations);
        byte[] plaintext = Encoding.UTF8.GetBytes("s3cr3t-P@ssw0rd ✓ ünïcödé");

        var blob = VaultCryptoService.Encrypt(key, plaintext);
        byte[] decrypted = VaultCryptoService.Decrypt(key, blob);

        Assert.Equal(plaintext, decrypted);
        Assert.NotEqual(plaintext, blob.Ciphertext);
    }

    [Fact]
    public void EncryptString_DecryptString_RoundTripsThroughSerializedBlob()
    {
        byte[] key = VaultCryptoService.DeriveKey("master", VaultCryptoService.GenerateSalt(), TestIterations);
        byte[] aad = "entry-id"u8.ToArray();

        byte[] stored = VaultCryptoService.EncryptString(key, "{\"Password\":\"hunter2\"}", aad);
        string back = VaultCryptoService.DecryptString(key, stored, aad);

        Assert.Equal("{\"Password\":\"hunter2\"}", back);
    }

    [Fact]
    public void Decrypt_WithKeyFromWrongPassword_Throws()
    {
        byte[] salt = VaultCryptoService.GenerateSalt();
        byte[] rightKey = VaultCryptoService.DeriveKey("right-password", salt, TestIterations);
        byte[] wrongKey = VaultCryptoService.DeriveKey("wrong-password", salt, TestIterations);
        var blob = VaultCryptoService.Encrypt(rightKey, "top secret"u8);

        Assert.ThrowsAny<CryptographicException>(() => VaultCryptoService.Decrypt(wrongKey, blob));
    }

    [Fact]
    public void VerifyKey_RejectsWrongPassword_AcceptsRightPassword()
    {
        byte[] salt = VaultCryptoService.GenerateSalt();
        byte[] key = VaultCryptoService.DeriveKey("right-password", salt, TestIterations);
        byte[] verifier = VaultCryptoService.CreateKeyVerifier(key);

        Assert.True(VaultCryptoService.VerifyKey(VaultCryptoService.DeriveKey("right-password", salt, TestIterations), verifier));
        Assert.False(VaultCryptoService.VerifyKey(VaultCryptoService.DeriveKey("Right-password", salt, TestIterations), verifier));
        Assert.False(VaultCryptoService.VerifyKey(VaultCryptoService.DeriveKey("", salt, TestIterations), verifier));
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        byte[] key = VaultCryptoService.DeriveKey("pw", VaultCryptoService.GenerateSalt(), TestIterations);
        var blob = VaultCryptoService.Encrypt(key, "important"u8);
        blob.Ciphertext[0] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => VaultCryptoService.Decrypt(key, blob));
    }

    [Fact]
    public void Decrypt_WithDifferentAssociatedData_Throws()
    {
        byte[] key = VaultCryptoService.DeriveKey("pw", VaultCryptoService.GenerateSalt(), TestIterations);
        var blob = VaultCryptoService.Encrypt(key, "data"u8, "entry-A"u8);

        Assert.ThrowsAny<CryptographicException>(() => VaultCryptoService.Decrypt(key, blob, "entry-B"u8));
    }

    [Fact]
    public void Encrypt_UsesUniqueNoncePerCall()
    {
        byte[] key = VaultCryptoService.DeriveKey("pw", VaultCryptoService.GenerateSalt(), TestIterations);
        var nonces = Enumerable.Range(0, 200)
            .Select(_ => Convert.ToHexString(VaultCryptoService.Encrypt(key, "same"u8).Nonce))
            .ToHashSet();

        Assert.Equal(200, nonces.Count);
    }

    [Fact]
    public void DeriveKey_IsDeterministic_And256Bit_AndSaltDependent()
    {
        byte[] salt = VaultCryptoService.GenerateSalt();
        byte[] a = VaultCryptoService.DeriveKey("pw", salt, TestIterations);
        byte[] b = VaultCryptoService.DeriveKey("pw", salt, TestIterations);
        byte[] c = VaultCryptoService.DeriveKey("pw", VaultCryptoService.GenerateSalt(), TestIterations);

        Assert.Equal(32, a.Length);
        Assert.Equal(32, VaultCryptoService.GenerateSalt().Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void DeriveKey_DefaultIterations_Is600k()
    {
        Assert.Equal(600_000, VaultCryptoService.DefaultIterations);
        // Full-strength derivation works and matches an explicit call.
        byte[] salt = VaultCryptoService.GenerateSalt();
        Assert.Equal(VaultCryptoService.DeriveKey("pw", salt), VaultCryptoService.DeriveKey("pw", salt, 600_000));
    }

    [Fact]
    public void EncryptedBlob_SerializationRoundTrips()
    {
        byte[] key = VaultCryptoService.DeriveKey("pw", VaultCryptoService.GenerateSalt(), TestIterations);
        var blob = VaultCryptoService.Encrypt(key, "payload"u8);
        var parsed = EncryptedBlob.FromBytes(blob.ToBytes());

        Assert.Equal(blob.Nonce, parsed.Nonce);
        Assert.Equal(blob.Tag, parsed.Tag);
        Assert.Equal(blob.Ciphertext, parsed.Ciphertext);
        Assert.Throws<CryptographicException>(() => EncryptedBlob.FromBytes(new byte[10]));
    }
}
