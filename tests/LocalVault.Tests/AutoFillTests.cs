using System.Runtime.InteropServices;
using System.Windows.Input;
using LocalVault.Interop;
using LocalVault.Models;
using LocalVault.Services;

namespace LocalVault.Tests;

public class CredentialMatcherTests
{
    private static FillTarget Browser(string url, string title = "") =>
        new(IntPtr.Zero, 0, "chrome", @"C:\chrome.exe", title, url, IsBrowser: true);

    private static FillTarget App(string process, string title = "", string path = "") =>
        new(IntPtr.Zero, 0, process, path, title, "", IsBrowser: false);

    private static VaultEntry Site(string title, string url, string user = "u") =>
        new() { Title = title, Url = url, Username = user, Password = "p", ProcessName = "chrome", Category = EntryCategory.Website };

    private static VaultEntry Desktop(string title, string process, string path = "") =>
        new() { Title = title, ProcessName = process, AppPath = path, Username = "u", Password = "p", Category = EntryCategory.DesktopApp };

    [Fact]
    public void Browser_ExactHost_IsSingleStrongMatch()
    {
        var entries = new[] { Site("GitHub", "https://github.com/login"), Site("GitLab", "https://gitlab.com") };
        var ranked = CredentialMatcher.Rank(Browser("https://github.com/session"), entries);

        var strong = Assert.Single(ranked, r => r.IsStrong);
        Assert.Equal("GitHub", strong.Entry.Title);
    }

    [Theory]
    [InlineData("https://accounts.google.com/signin", "https://google.com")]       // subdomain of saved
    [InlineData("https://google.com", "https://accounts.google.com")]               // saved is subdomain
    [InlineData("https://mail.google.com", "https://accounts.google.com")]          // same registrable domain
    [InlineData("https://www.bbc.co.uk/account", "https://session.bbc.co.uk")]      // two-level public suffix
    public void Browser_RelatedDomains_AreStrong(string pageUrl, string savedUrl)
    {
        Assert.True(CredentialMatcher.Score(Browser(pageUrl), Site("X", savedUrl)) >= CredentialMatcher.StrongScore);
    }

    [Fact]
    public void Browser_DifferentDomain_SharingSuffix_DoesNotMatch()
    {
        // "evilgithub.com" must never match "github.com".
        Assert.Equal(0, CredentialMatcher.Score(Browser("https://evilgithub.com"), Site("GitHub", "https://github.com")));
        Assert.Equal(0, CredentialMatcher.Score(Browser("https://example.co.uk"), Site("Other", "https://other.co.uk")));
    }

    [Fact]
    public void Browser_DoesNotMatchByBrowserProcessName()
    {
        // Every website entry was captured from chrome — that alone must not make it a match.
        Assert.Equal(0, CredentialMatcher.Score(Browser("https://news.example.org"), Site("Bank", "https://bank.com")));
    }

    [Fact]
    public void Browser_WithoutUrl_FallsBackToWeakTitleMatch()
    {
        var score = CredentialMatcher.Score(Browser("", "Sign in - GitHub - Google Chrome"), Site("GitHub", "https://github.com"));
        Assert.InRange(score, 1, CredentialMatcher.StrongScore - 1);
    }

    [Fact]
    public void Desktop_MatchesByProcessOrExePath()
    {
        Assert.True(CredentialMatcher.Score(App("Steam"), Desktop("Steam", "steam")) >= CredentialMatcher.StrongScore);
        Assert.True(CredentialMatcher.Score(App("Discord", path: @"C:\Users\x\Discord.exe"), Desktop("Discord", "", @"D:\Apps\discord.exe"))
                    >= CredentialMatcher.StrongScore);
        Assert.Equal(0, CredentialMatcher.Score(App("notepad"), Desktop("Steam", "steam")));
    }

    [Fact]
    public void Desktop_TitleOnlyMatch_IsWeak()
    {
        int score = CredentialMatcher.Score(App("javaw", "Minecraft Launcher"), Desktop("Minecraft", "launcher"));
        Assert.InRange(score, 1, CredentialMatcher.StrongScore - 1);
    }

    [Fact]
    public void MultipleAccountsForSameSite_AreAllStrong_SoPickerIsShown()
    {
        var entries = new[] { Site("GitHub work", "https://github.com", "work"), Site("GitHub personal", "https://github.com", "me") };
        Assert.Equal(2, CredentialMatcher.Rank(Browser("https://github.com"), entries).Count(r => r.IsStrong));
    }

    [Theory]
    [InlineData("login.github.com", "github.com")]
    [InlineData("a.b.example.co.uk", "example.co.uk")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("localhost", "localhost")]
    public void RegistrableDomain_Heuristic(string host, string expected) =>
        Assert.Equal(expected, CredentialMatcher.RegistrableDomain(host));
}

public class HotkeyTests
{
    [Fact]
    public void Default_IsCtrlShiftL()
    {
        Assert.Equal("Ctrl+Shift+L", Hotkey.Default.ToString());
        Assert.Equal(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, Hotkey.Default.NativeModifiers);
        Assert.Equal(0x4Cu, Hotkey.Default.VirtualKey); // VK_L
    }

    [Theory]
    [InlineData("Ctrl+Shift+L", ModifierKeys.Control | ModifierKeys.Shift, Key.L)]
    [InlineData("alt + win + F2", ModifierKeys.Alt | ModifierKeys.Windows, Key.F2)]
    [InlineData("Ctrl+Alt+7", ModifierKeys.Control | ModifierKeys.Alt, Key.D7)]
    public void TryParse_RoundTrips(string text, ModifierKeys mods, Key key)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(new Hotkey(mods, key), hk);
        Assert.True(Hotkey.TryParse(hk.ToString(), out var again));
        Assert.Equal(hk, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("L")]              // no modifier
    [InlineData("Shift+L")]        // Shift alone would hijack normal typing
    [InlineData("Ctrl+Shift")]     // no key
    [InlineData("Ctrl+Banana")]
    public void TryParse_RejectsInvalid(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void SendInputStruct_HasNativeSize()
    {
        // INPUT is 40 bytes on x64 / 28 on x86; a wrong size makes SendInput fail silently.
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
