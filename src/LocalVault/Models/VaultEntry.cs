using System.Text.Json.Serialization;

namespace LocalVault.Models;

public enum EntryCategory
{
    Website = 0,
    DesktopApp = 1,
}

/// <summary>A single credential. Persisted as one AES-GCM encrypted JSON document.</summary>
public sealed class VaultEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Website URL (for websites) — may be empty.</summary>
    public string Url { get; set; } = "";
    /// <summary>Executable path (for desktop apps) — may be empty.</summary>
    public string AppPath { get; set; } = "";
    /// <summary>Process name the credential was captured from, e.g. "steam".</summary>
    public string ProcessName { get; set; } = "";
    public string Notes { get; set; } = "";
    public EntryCategory Category { get; set; } = EntryCategory.Website;
    public bool IsFavorite { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public string Subtitle => string.IsNullOrEmpty(Username) ? Host : Username;

    [JsonIgnore]
    public string Host => TryGetHost(Url) ?? (string.IsNullOrEmpty(ProcessName) ? "" : ProcessName + ".exe");

    [JsonIgnore]
    public string Initial => string.IsNullOrWhiteSpace(Title) ? "?" : Title.Trim()[..1].ToUpperInvariant();

    public VaultEntry Clone() => (VaultEntry)MemberwiseClone();

    public static string? TryGetHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        string candidate = url.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = "https://" + candidate;
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            string host = uri.Host.ToLowerInvariant();
            return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
        }
        return null;
    }
}
