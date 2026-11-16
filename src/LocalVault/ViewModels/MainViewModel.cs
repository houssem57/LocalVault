using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalVault.Models;
using LocalVault.Services;
using LocalVault.Views;

namespace LocalVault.ViewModels;

public enum VaultFilter
{
    All,
    Favorites,
    Websites,
    DesktopApps,
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly VaultService _vault;
    private readonly ClipboardService _clipboard;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public ObservableCollection<VaultEntry> Entries { get; } = [];
    public ICollectionView EntriesView { get; }

    [ObservableProperty] private bool _isInitialized;
    [ObservableProperty] private bool _isUnlocked;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _lockError = "";

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private VaultFilter _filter = VaultFilter.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(DisplayPassword))]
    [NotifyCanExecuteChangedFor(nameof(CopyUsernameCommand), nameof(CopyPasswordCommand), nameof(EditCommand),
        nameof(DeleteCommand), nameof(LaunchCommand), nameof(ToggleFavoriteCommand), nameof(ToggleRevealCommand))]
    private VaultEntry? _selectedEntry;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayPassword))]
    private bool _isPasswordRevealed;

    [ObservableProperty] private string _statusMessage = "";

    [ObservableProperty] private int _countAll;
    [ObservableProperty] private int _countFavorites;
    [ObservableProperty] private int _countWebsites;
    [ObservableProperty] private int _countApps;

    public bool HasSelection => SelectedEntry is not null;

    public string DisplayPassword => SelectedEntry is null ? ""
        : IsPasswordRevealed ? SelectedEntry.Password
        : new string('•', Math.Clamp(SelectedEntry.Password.Length, 8, 16));

    public MainViewModel(VaultService vault, ClipboardService clipboard, SettingsService settings)
    {
        _vault = vault;
        _clipboard = clipboard;
        _settings = settings;

        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = o => o is VaultEntry e && MatchesFilter(e);
        EntriesView.SortDescriptions.Add(new SortDescription(nameof(VaultEntry.Title), ListSortDirection.Ascending));

        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusMessage = ""; };
        _clipboard.Cleared += (_, _) => ShowStatus("Clipboard cleared");

        _vault.EntriesChanged += (_, _) => Reload();
        _vault.Locked += (_, _) => { IsUnlocked = false; IsPasswordRevealed = false; SelectedEntry = null; };
        _vault.Unlocked += (_, _) => { IsUnlocked = true; IsInitialized = true; LockError = ""; };

        IsInitialized = _vault.IsInitialized;
        IsUnlocked = _vault.IsUnlocked;
    }

    partial void OnSearchTextChanged(string value) => EntriesView.Refresh();
    partial void OnFilterChanged(VaultFilter value) => EntriesView.Refresh();
    partial void OnSelectedEntryChanged(VaultEntry? value) => IsPasswordRevealed = false;

    private bool MatchesFilter(VaultEntry e)
    {
        bool category = Filter switch
        {
            VaultFilter.Favorites => e.IsFavorite,
            VaultFilter.Websites => e.Category == EntryCategory.Website,
            VaultFilter.DesktopApps => e.Category == EntryCategory.DesktopApp,
            _ => true,
        };
        if (!category) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        string q = SearchText.Trim();
        return e.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || e.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
            || e.Url.Contains(q, StringComparison.OrdinalIgnoreCase)
            || e.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || e.Notes.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Reload()
    {
        Guid? selectedId = SelectedEntry?.Id;
        bool revealed = IsPasswordRevealed;
        Entries.Clear();
        foreach (var e in _vault.Entries) Entries.Add(e);
        CountAll = Entries.Count;
        CountFavorites = Entries.Count(e => e.IsFavorite);
        CountWebsites = Entries.Count(e => e.Category == EntryCategory.Website);
        CountApps = Entries.Count(e => e.Category == EntryCategory.DesktopApp);
        SelectedEntry = selectedId is { } id ? Entries.FirstOrDefault(e => e.Id == id) : null;
        if (SelectedEntry is not null) IsPasswordRevealed = revealed;
    }

    public void ShowStatus(string message)
    {
        StatusMessage = message;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // ───────────── Lock screen ─────────────

    public async Task<bool> UnlockAsync(string masterPassword)
    {
        if (string.IsNullOrEmpty(masterPassword)) { LockError = "Enter your master password."; return false; }
        IsBusy = true;
        LockError = "";
        try
        {
            bool ok = await Task.Run(() => _vault.TryUnlockCore(masterPassword));
            if (ok) _vault.CompleteUnlock();
            else LockError = "Incorrect master password.";
            return ok;
        }
        catch (Exception ex)
        {
            LockError = "Could not open the vault: " + ex.Message;
            return false;
        }
        finally { IsBusy = false; }
    }

    public async Task<bool> CreateVaultAsync(string password, string confirm)
    {
        if (password.Length < VaultService.MinimumMasterPasswordLength)
        { LockError = $"Use at least {VaultService.MinimumMasterPasswordLength} characters."; return false; }
        if (password != confirm) { LockError = "Passwords do not match."; return false; }
        IsBusy = true;
        LockError = "";
        try
        {
            await Task.Run(() => _vault.CreateVaultCore(password));
            _vault.CompleteUnlock();
            return true;
        }
        catch (Exception ex)
        {
            LockError = ex.Message;
            return false;
        }
        finally { IsBusy = false; }
    }

    // ───────────── Commands ─────────────

    [RelayCommand]
    private void SetFilter(VaultFilter filter) => Filter = filter;

    [RelayCommand]
    private void Lock() => _vault.Lock();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyUsername()
    {
        if (SelectedEntry is null || string.IsNullOrEmpty(SelectedEntry.Username)) return;
        _clipboard.CopySecret(SelectedEntry.Username, TimeSpan.FromSeconds(_settings.Current.ClipboardClearSeconds));
        ShowStatus("Username copied");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPassword()
    {
        if (SelectedEntry is null || string.IsNullOrEmpty(SelectedEntry.Password)) return;
        int seconds = _settings.Current.ClipboardClearSeconds;
        _clipboard.CopySecret(SelectedEntry.Password, TimeSpan.FromSeconds(seconds));
        ShowStatus($"Password copied — clipboard clears in {seconds}s");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleReveal() => IsPasswordRevealed = !IsPasswordRevealed;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleFavorite()
    {
        if (SelectedEntry is null) return;
        var copy = SelectedEntry.Clone();
        copy.IsFavorite = !copy.IsFavorite;
        _vault.Save(copy);
    }

    [RelayCommand]
    private void Add()
    {
        var created = EntryEditorWindow.Show(Owner, null);
        if (created is null) return;
        var saved = _vault.Save(created);
        SelectedEntry = Entries.FirstOrDefault(e => e.Id == saved.Id);
        ShowStatus("Item added");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Edit()
    {
        if (SelectedEntry is null) return;
        var edited = EntryEditorWindow.Show(Owner, SelectedEntry);
        if (edited is null) return;
        _vault.Save(edited);
        ShowStatus("Changes saved");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        if (SelectedEntry is null) return;
        var result = MessageBox.Show(Owner, $"Delete \"{SelectedEntry.Title}\"? This cannot be undone.", "Delete item",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;
        _vault.Delete(SelectedEntry.Id);
        SelectedEntry = null;
        ShowStatus("Item deleted");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Launch()
    {
        if (SelectedEntry is null) return;
        try
        {
            if (SelectedEntry.Category == EntryCategory.DesktopApp && File.Exists(SelectedEntry.AppPath))
            {
                Process.Start(new ProcessStartInfo(SelectedEntry.AppPath) { UseShellExecute = true });
                return;
            }
            string url = SelectedEntry.Url.Trim();
            if (url.Length == 0) { ShowStatus("No URL or app path for this item"); return; }
            if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            else
                ShowStatus("Only http(s) links can be launched");
        }
        catch (Exception ex)
        {
            ShowStatus("Could not launch: " + ex.Message);
        }
    }

    [RelayCommand]
    private void OpenGenerator() => PasswordGeneratorWindow.ShowTool(Owner, _clipboard, _settings);

    [RelayCommand]
    private void OpenSettings() => SettingsWindow.ShowDialog(Owner, _settings, _vault.DatabasePath);

    private static Window? Owner => Application.Current.MainWindow is { IsVisible: true } w ? w : null;
}
