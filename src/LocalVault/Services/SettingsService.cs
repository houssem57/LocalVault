using System.Text.Json;

namespace LocalVault.Services;

public sealed class AppSettings
{
    public int AutoLockMinutes { get; set; } = 15;
    public bool DetectionEnabled { get; set; } = true;
    public int ClipboardClearSeconds { get; set; } = 30;
    /// <summary>Hosts (websites) or process names (desktop apps) the user chose "Never" for.</summary>
    public List<string> NeverSave { get; set; } = [];
    public bool TrayHintShown { get; set; }
    /// <summary>Global auto-fill hotkey, e.g. "Ctrl+Shift+L".</summary>
    public string AutoFillHotkey { get; set; } = "Ctrl+Shift+L";
    /// <summary>Press Enter after filling.</summary>
    public bool AutoSubmit { get; set; }
    /// <summary>Always fill via clipboard paste instead of simulated typing (for apps that ignore typed input).</summary>
    public bool AlwaysPaste { get; set; }
}

/// <summary>Non-secret preferences stored as JSON next to the vault.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public AppSettings Current { get; private set; } = new();

    public SettingsService(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataDirectory, "settings.json");
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Current = new AppSettings();
        }
        Current.AutoLockMinutes = Math.Clamp(Current.AutoLockMinutes, 1, 240);
        Current.ClipboardClearSeconds = Math.Clamp(Current.ClipboardClearSeconds, 5, 300);
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOptions));
    }

    public bool IsNeverSave(string key) =>
        !string.IsNullOrEmpty(key) && Current.NeverSave.Contains(key, StringComparer.OrdinalIgnoreCase);

    public void AddNeverSave(string key)
    {
        if (string.IsNullOrEmpty(key) || IsNeverSave(key)) return;
        Current.NeverSave.Add(key);
        Save();
    }
}
