using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LocalVault.Models;
using LocalVault.Services;

namespace LocalVault.Views;

/// <summary>Non-activating, topmost "Save password for X?" prompt shown in the bottom-right corner.</summary>
public partial class SaveCredentialToast : Window
{
    private readonly CapturedCredential _credential;
    private readonly VaultService _vault;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _autoClose = new() { Interval = TimeSpan.FromMinutes(2) };
    private readonly EventHandler _onUnlocked;

    public SaveCredentialToast(CapturedCredential credential, VaultService vault, SettingsService settings)
    {
        InitializeComponent();
        _credential = credential;
        _vault = vault;
        _settings = settings;

        string target = credential.IsBrowser && credential.Host.Length > 0 ? credential.Host : credential.Title;
        HeadingText.Text = $"Save password for {target}?";
        TitleBox.Text = credential.Title;
        UsernameBox.Text = credential.Username;
        PasswordBox.Password = credential.Password;
        SourceText.Text = credential.IsBrowser
            ? $"{credential.ProcessName} · {(credential.Url.Length > 0 ? credential.Url : credential.WindowTitle)}"
            : $"{credential.ProcessName}.exe · {credential.WindowTitle}";
        NeverButton.Content = credential.IsBrowser ? "Never for this site" : "Never for this app";

        _onUnlocked = (_, _) => Dispatcher.BeginInvoke(RefreshLockState);
        _vault.Unlocked += _onUnlocked;
        RefreshLockState();

        _autoClose.Tick += (_, _) => { if (!IsMouseOver && !IsKeyboardFocusWithin) Close(); };
        Loaded += (_, _) => { PositionBottomRight(); _autoClose.Start(); };
        SizeChanged += (_, _) => PositionBottomRight();
        Closed += (_, _) =>
        {
            _autoClose.Stop();
            _vault.Unlocked -= _onUnlocked;
            PasswordBox.Clear();
            PasswordText.Clear();
        };
    }

    private void RefreshLockState()
    {
        bool locked = !_vault.IsUnlocked;
        UnlockPanel.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Content = locked ? "Unlock & save" : "Save";

        if (!locked)
        {
            var existing = _vault.FindMatch(_credential.Host, _credential.ProcessName, _credential.Username);
            if (existing is not null)
            {
                HeadingText.Text = $"Update password for {existing.Title}?";
                SaveButton.Content = "Update";
            }
        }
    }

    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
    }

    private string CurrentPassword => RevealToggle.IsChecked == true ? PasswordText.Text : PasswordBox.Password;

    private void RevealToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (RevealToggle.IsChecked == true)
        {
            PasswordText.Text = PasswordBox.Password;
            PasswordText.Visibility = Visibility.Visible;
            PasswordBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            PasswordBox.Password = PasswordText.Text;
            PasswordText.Clear();
            PasswordBox.Visibility = Visibility.Visible;
            PasswordText.Visibility = Visibility.Collapsed;
        }
    }

    private void MasterBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Save_Click(sender, e);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked)
        {
            string master = MasterBox.Password;
            if (master.Length == 0) { MasterBox.Focus(); return; }
            SaveButton.IsEnabled = false;
            bool ok;
            try { ok = await Task.Run(() => _vault.TryUnlockCore(master)); }
            catch (Exception) { ok = false; }
            SaveButton.IsEnabled = true;
            MasterBox.Clear();
            if (!ok)
            {
                UnlockError.Visibility = Visibility.Visible;
                MasterBox.Focus();
                return;
            }
            _vault.CompleteUnlock();
        }

        string password = CurrentPassword;
        if (password.Length == 0) return;

        var existing = _vault.FindMatch(_credential.Host, _credential.ProcessName, UsernameBox.Text.Trim());
        var entry = existing?.Clone() ?? new VaultEntry
        {
            Category = _credential.Category,
            Url = _credential.IsBrowser ? NormalizeUrl(_credential.Url) : "",
            AppPath = _credential.IsBrowser ? "" : _credential.ProcessPath,
            ProcessName = _credential.ProcessName,
        };
        entry.Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? _credential.Title : TitleBox.Text.Trim();
        entry.Username = UsernameBox.Text.Trim();
        entry.Password = password;
        _vault.Save(entry);
        Close();
    }

    private static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        url = url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        // Keep scheme + host + path, drop query/fragment (may contain tokens).
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : url;
    }

    private void Never_Click(object sender, RoutedEventArgs e)
    {
        _settings.AddNeverSave(_credential.TargetKey);
        Close();
    }

    private void Ignore_Click(object sender, RoutedEventArgs e) => Close();
}
