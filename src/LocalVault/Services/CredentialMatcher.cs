using LocalVault.Models;

namespace LocalVault.Services;

/// <summary>The window the user pressed the auto-fill hotkey in, captured before any LocalVault UI appears.</summary>
public sealed record FillTarget(
    IntPtr Hwnd,
    uint ProcessId,
    string ProcessName,
    string ProcessPath,
    string WindowTitle,
    string Url,
    bool IsBrowser)
{
    public string Host => VaultEntry.TryGetHost(Url) ?? "";

    public string Describe() => IsBrowser
        ? (Host.Length > 0 ? Host : WindowTitle)
        : (WindowTitle.Length > 0 ? $"{WindowTitle} ({ProcessName}.exe)" : ProcessName + ".exe");
}

public sealed record ScoredEntry(VaultEntry Entry, int Score)
{
    /// <summary>Domain or executable match — reliable enough to fill without asking.</summary>
    public bool IsStrong => Score >= CredentialMatcher.StrongScore;
}

/// <summary>Ranks vault entries against a fill target (pure logic, unit-tested).</summary>
public static class CredentialMatcher
{
    public const int StrongScore = 80;

    public static IReadOnlyList<ScoredEntry> Rank(FillTarget target, IEnumerable<VaultEntry> entries) =>
        entries.Select(e => new ScoredEntry(e, Score(target, e)))
               .Where(s => s.Score > 0)
               .OrderByDescending(s => s.Score)
               .ThenByDescending(s => s.Entry.IsFavorite)
               .ThenBy(s => s.Entry.Title, StringComparer.CurrentCultureIgnoreCase)
               .ToList();

    public static int Score(FillTarget target, VaultEntry entry)
    {
        int score = 0;
        string title = target.WindowTitle;

        if (target.IsBrowser)
        {
            string entryHost = VaultEntry.TryGetHost(entry.Url) ?? "";
            if (target.Host.Length > 0 && entryHost.Length > 0)
            {
                if (string.Equals(target.Host, entryHost, StringComparison.OrdinalIgnoreCase)) score = 100;
                else if (IsSubdomainOf(target.Host, entryHost) || IsSubdomainOf(entryHost, target.Host)) score = 95;
                else if (string.Equals(RegistrableDomain(target.Host), RegistrableDomain(entryHost), StringComparison.OrdinalIgnoreCase)) score = 85;
            }
            // No URL available (or no URL match): weak hint from the page title.
            if (score == 0 && title.Length > 0)
            {
                if (entryHost.Length > 0 && title.Contains(RegistrableLabel(entryHost), StringComparison.OrdinalIgnoreCase)) score = 50;
                else if (entry.Title.Length >= 3 && title.Contains(entry.Title, StringComparison.OrdinalIgnoreCase)) score = 40;
            }
            return score;
        }

        // Desktop app.
        if (entry.Category == EntryCategory.DesktopApp)
        {
            if (entry.ProcessName.Length > 0 && string.Equals(entry.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase))
                score = 90;
            else if (entry.AppPath.Length > 0 && target.ProcessPath.Length > 0 &&
                     string.Equals(Path.GetFileName(entry.AppPath), Path.GetFileName(target.ProcessPath), StringComparison.OrdinalIgnoreCase))
                score = 90;
        }
        if (score == 0 && entry.Title.Length >= 3 && title.Contains(entry.Title, StringComparison.OrdinalIgnoreCase))
            score = 40;
        return score;
    }

    private static bool IsSubdomainOf(string host, string parent) =>
        host.EndsWith("." + parent, StringComparison.OrdinalIgnoreCase);

    /// <summary>"accounts.google.co.uk" → "google.co.uk"; "login.github.com" → "github.com". Heuristic, no PSL.</summary>
    public static string RegistrableDomain(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out _)) return host;
        string[] labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length <= 2) return host;
        bool secondLevelSuffix = labels[^1].Length == 2 && labels[^2].Length <= 3; // co.uk, com.au, co.jp …
        int take = secondLevelSuffix ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }

    /// <summary>"github.com" → "github" (used to match page titles).</summary>
    private static string RegistrableLabel(string host)
    {
        string domain = RegistrableDomain(host);
        int dot = domain.IndexOf('.');
        return dot > 0 ? domain[..dot] : domain;
    }
}
