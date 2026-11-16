using LocalVault.Models;
using LocalVault.Services;

namespace LocalVault.Tests;

public sealed class VaultServiceTests : IDisposable
{
    private const int TestIterations = 10_000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LocalVaultTests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "vault.db");

    private VaultService NewService() => new(new VaultStore(DbPath), TestIterations);

    [Fact]
    public void CreateSaveReopenUnlock_RoundTripsEntries()
    {
        var vault = NewService();
        vault.CreateVault("master-password-1");
        vault.Save(new VaultEntry { Title = "Example", Username = "alice@example.com", Password = "p@ss", Url = "https://example.com/login" });
        vault.Lock();
        Assert.Empty(vault.Entries);

        var reopened = NewService();
        Assert.True(reopened.IsInitialized);
        Assert.True(reopened.TryUnlock("master-password-1"));
        var entry = Assert.Single(reopened.Entries);
        Assert.Equal("alice@example.com", entry.Username);
        Assert.Equal("p@ss", entry.Password);
        Assert.Equal("example.com", entry.Host);
    }

    [Fact]
    public void TryUnlock_WrongPassword_IsRejected_AndVaultStaysLocked()
    {
        var vault = NewService();
        vault.CreateVault("master-password-1");
        vault.Save(new VaultEntry { Title = "X", Password = "secret" });
        vault.Lock();

        var reopened = NewService();
        Assert.False(reopened.TryUnlock("master-password-2"));
        Assert.False(reopened.IsUnlocked);
        Assert.Empty(reopened.Entries);
        Assert.Throws<InvalidOperationException>(() => reopened.Save(new VaultEntry()));
    }

    [Fact]
    public void DatabaseFile_ContainsNoPlaintext()
    {
        var vault = NewService();
        vault.CreateVault("master-password-1");
        vault.Save(new VaultEntry { Title = "VerySecretTitle", Username = "plainuser", Password = "PlainTextPassword123" });
        vault.Lock();

        byte[] bytes = File.ReadAllBytes(DbPath);
        string walPath = DbPath + "-wal";
        if (File.Exists(walPath)) bytes = [.. bytes, .. File.ReadAllBytes(walPath)];
        string raw = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("PlainTextPassword123", raw);
        Assert.DoesNotContain("plainuser", raw);
        Assert.DoesNotContain("VerySecretTitle", raw);
    }

    [Fact]
    public void CreateVault_RejectsShortMasterPassword()
    {
        var vault = NewService();
        Assert.Throws<ArgumentException>(() => vault.CreateVault("short"));
        Assert.False(vault.IsInitialized);
    }

    [Fact]
    public void FindMatch_MatchesByHostOrProcess_AndUsername()
    {
        var vault = NewService();
        vault.CreateVault("master-password-1");
        vault.Save(new VaultEntry { Title = "GitHub", Username = "bob", Password = "a", Url = "https://www.github.com/login" });
        vault.Save(new VaultEntry { Title = "Steam", Username = "gamer", Password = "b", ProcessName = "steam", Category = EntryCategory.DesktopApp });

        Assert.NotNull(vault.FindMatch("github.com", "chrome", "bob"));
        Assert.Null(vault.FindMatch("github.com", "chrome", "carol"));
        Assert.NotNull(vault.FindMatch("", "Steam", "gamer"));
        Assert.Null(vault.FindMatch("", "discord", "gamer"));
    }

    [Fact]
    public void SaveUpdate_And_Delete_Persist()
    {
        var vault = NewService();
        vault.CreateVault("master-password-1");
        var saved = vault.Save(new VaultEntry { Title = "A", Password = "1" });
        var edited = saved.Clone();
        edited.Password = "2";
        vault.Save(edited);
        Assert.Single(vault.Entries);
        Assert.Equal("2", vault.Entries[0].Password);

        vault.Delete(saved.Id);
        vault.Lock();
        var reopened = NewService();
        Assert.True(reopened.TryUnlock("master-password-1"));
        Assert.Empty(reopened.Entries);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

public class PasswordGeneratorTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(32)]
    [InlineData(64)]
    public void Generate_RespectsLength_AndIncludesEverySelectedSet(int length)
    {
        for (int i = 0; i < 50; i++)
        {
            string pw = PasswordGenerator.Generate(new PasswordOptions(length));
            Assert.Equal(length, pw.Length);
            Assert.Contains(pw, char.IsUpper);
            Assert.Contains(pw, char.IsLower);
            Assert.Contains(pw, char.IsDigit);
            Assert.Contains(pw, c => !char.IsLetterOrDigit(c));
        }
    }

    [Fact]
    public void Generate_ClampsLength_AndHonorsDisabledSets()
    {
        string pw = PasswordGenerator.Generate(new PasswordOptions(4, Uppercase: false, Lowercase: true, Digits: false, Symbols: false));
        Assert.Equal(PasswordGenerator.MinLength, pw.Length);
        Assert.All(pw, c => Assert.True(char.IsLower(c)));
    }
}
