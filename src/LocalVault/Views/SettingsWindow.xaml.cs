using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using LocalVault.Services;

namespace LocalVault.Views;

public partial class SettingsWindow : Window
{
    private static readonly int[] AutoLockOptions = [1, 5, 10, 15, 30, 60, 120, 240];
    private static readonly int[] ClipboardOptions = [10, 20, 30, 60, 120];

    /// <summary>Raised when the user changes the detection setting (so the tray menu can sync).</summary>
    public static event Action<bool>? DetectionChanged;

    /// <summary>Set by the app: tries to register a new auto-fill hotkey; returns false on a conflict (old one is kept).</summary>
    public static Func<Hotkey, bool>? ApplyHotkey;

    /// <summary>Set by the app: pauses the global hotkey while the user is recording a new one.</summary>
    public static Action<bool>? SuspendHotkey;

    private Hotkey _hotkey;

    private readonly SettingsService _settings;
    private readonly string _dbPath;

    private SettingsWindow(SettingsService settings, string dbPath)
    {
        InitializeComponent();
        _settings = settings;
        _dbPath = dbPath;
        var s = settings.Current;

        foreach (int m in AutoLockOptions) AutoLockBox.Items.Add(m < 60 ? $"{m} minute{(m == 1 ? "" : "s")}" : $"{m / 60} hour{(m == 60 ? "" : "s")}");
        AutoLockBox.SelectedIndex = Math.Max(0, Array.IndexOf(AutoLockOptions, s.AutoLockMinutes) is var i && i >= 0 ? i : 3);

        foreach (int sec in ClipboardOptions) ClipboardBox.Items.Add($"{sec} seconds");
        ClipboardBox.SelectedIndex = Array.IndexOf(ClipboardOptions, s.ClipboardClearSeconds) is var j && j >= 0 ? j : 2;

        StartupBox.IsChecked = StartupService.IsEnabled;
        DetectBox.IsChecked = s.DetectionEnabled;
        foreach (string n in s.NeverSave) NeverList.Items.Add(n);
        PathText.Text = dbPath;

        _hotkey = Hotkey.TryParse(s.AutoFillHotkey, out var hk) ? hk : Hotkey.Default;
        HotkeyBox.Text = _hotkey.ToString();
        AutoSubmitBox.IsChecked = s.AutoSubmit;
        AlwaysPasteBox.IsChecked = s.AlwaysPaste;
        HotkeyBox.GotKeyboardFocus += (_, _) => SuspendHotkey?.Invoke(true);
        HotkeyBox.LostKeyboardFocus += (_, _) => SuspendHotkey?.Invoke(false);
        Closed += (_, _) => SuspendHotkey?.Invoke(false);
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = false; return; } // keep Tab navigation
        var mods = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= ModifierKeys.Windows;
        var candidate = new Hotkey(mods, key);
        if (candidate.IsValid)
        {
            _hotkey = candidate;
            HotkeyBox.Text = candidate.ToString();
            SetHotkeyStatus($"New hotkey: {candidate}. Click Save to apply.", error: false);
        }
        else if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin))
        {
            SetHotkeyStatus("Use at least Ctrl, Alt or Win together with a key.", error: true);
        }
    }

    private void ResetHotkey_Click(object sender, RoutedEventArgs e)
    {
        _hotkey = Hotkey.Default;
        HotkeyBox.Text = _hotkey.ToString();
        SetHotkeyStatus($"Hotkey reset to {_hotkey}. Click Save to apply.", error: false);
    }

    private void SetHotkeyStatus(string text, bool error)
    {
        HotkeyStatus.Text = text;
        HotkeyStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            error ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
    }

    public static void ShowDialog(Window? owner, SettingsService settings, string dbPath)
    {
        var w = new SettingsWindow(settings, dbPath);
        if (owner is not null) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
    }

    private void RemoveNever_Click(object sender, RoutedEventArgs e)
    {
        if (NeverList.SelectedItem is not null) NeverList.Items.Remove(NeverList.SelectedItem);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string? dir = Path.GetDirectoryName(_dbPath);
        if (dir is not null && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Validate the hotkey first so nothing else is changed if it conflicts.
        if (ApplyHotkey is not null && !ApplyHotkey(_hotkey))
        {
            SetHotkeyStatus($"{_hotkey} is already used by another app. Choose a different combination.", error: true);
            HotkeyBox.Focus();
            return;
        }
        var s = _settings.Current;
        s.AutoLockMinutes = AutoLockOptions[Math.Max(0, AutoLockBox.SelectedIndex)];
        s.ClipboardClearSeconds = ClipboardOptions[Math.Max(0, ClipboardBox.SelectedIndex)];
        s.NeverSave = NeverList.Items.Cast<string>().ToList();
        bool detect = DetectBox.IsChecked == true;
        s.AutoSubmit = AutoSubmitBox.IsChecked == true;
        s.AlwaysPaste = AlwaysPasteBox.IsChecked == true;

        s.AutoFillHotkey = _hotkey.ToString();
        _settings.Save();

        try { StartupService.SetEnabled(StartupBox.IsChecked == true); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not update the startup setting: " + ex.Message, "LocalVault",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        DetectionChanged?.Invoke(detect);
        DialogResult = true;
    }
}
