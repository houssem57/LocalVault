using System.Windows;
using System.Windows.Controls;
using LocalVault.Models;
using Microsoft.Win32;

namespace LocalVault.Views;

public partial class EntryEditorWindow : Window
{
    private readonly VaultEntry _entry;

    public VaultEntry? Result { get; private set; }

    private EntryEditorWindow(VaultEntry? existing)
    {
        InitializeComponent();
        _entry = existing?.Clone() ?? new VaultEntry();
        Heading.Text = existing is null ? "New item" : "Edit item";
        Title = existing is null ? "New item" : "Edit item";

        TitleBox.Text = _entry.Title;
        UsernameBox.Text = _entry.Username;
        PasswordBox.Password = _entry.Password;
        UrlBox.Text = _entry.Url;
        AppPathBox.Text = _entry.AppPath;
        NotesBox.Text = _entry.Notes;
        FavoriteBox.IsChecked = _entry.IsFavorite;
        CategoryBox.SelectedIndex = _entry.Category == EntryCategory.DesktopApp ? 1 : 0;

        Loaded += (_, _) => TitleBox.Focus();
    }

    /// <summary>Shows the editor; returns the edited copy, or null if cancelled.</summary>
    public static VaultEntry? Show(Window? owner, VaultEntry? existing)
    {
        var w = new EntryEditorWindow(existing);
        if (owner is not null) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return w.ShowDialog() == true ? w.Result : null;
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

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UrlPanel is null || AppPanel is null) return;
        bool app = CategoryBox.SelectedIndex == 1;
        AppPanel.Visibility = app ? Visibility.Visible : Visibility.Collapsed;
        UrlPanel.Visibility = app ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        string? generated = PasswordGeneratorWindow.Pick(this);
        if (generated is null) return;
        PasswordBox.Password = generated;
        if (RevealToggle.IsChecked == true) PasswordText.Text = generated;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == true)
        {
            AppPathBox.Text = dlg.FileName;
            if (string.IsNullOrWhiteSpace(TitleBox.Text))
                TitleBox.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            string? host = VaultEntry.TryGetHost(UrlBox.Text);
            title = host ?? "";
        }
        if (title.Length == 0)
        {
            ErrorText.Text = "Give this item a title.";
            ErrorText.Visibility = Visibility.Visible;
            TitleBox.Focus();
            return;
        }

        _entry.Title = title;
        _entry.Username = UsernameBox.Text.Trim();
        _entry.Password = CurrentPassword;
        _entry.Category = CategoryBox.SelectedIndex == 1 ? EntryCategory.DesktopApp : EntryCategory.Website;
        _entry.Url = _entry.Category == EntryCategory.Website ? UrlBox.Text.Trim() : _entry.Url;
        _entry.AppPath = _entry.Category == EntryCategory.DesktopApp ? AppPathBox.Text.Trim() : _entry.AppPath;
        _entry.Notes = NotesBox.Text;
        _entry.IsFavorite = FavoriteBox.IsChecked == true;
        if (_entry.Category == EntryCategory.DesktopApp && string.IsNullOrEmpty(_entry.ProcessName) && _entry.AppPath.Length > 0)
            _entry.ProcessName = System.IO.Path.GetFileNameWithoutExtension(_entry.AppPath);

        Result = _entry;
        DialogResult = true;
    }
}
