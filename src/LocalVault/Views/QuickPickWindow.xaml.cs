using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalVault.Models;
using LocalVault.Services;
using Forms = System.Windows.Forms;

namespace LocalVault.Views;

public sealed record QuickPickItem(VaultEntry Entry, bool IsSuggested);

/// <summary>Spotlight-style floating picker: best matches first, live search across the whole vault.</summary>
public partial class QuickPickWindow : Window
{
    private readonly IReadOnlyList<VaultEntry> _all;
    private readonly HashSet<Guid> _suggested;
    private readonly TaskCompletionSource<VaultEntry?> _result = new();

    private QuickPickWindow(FillTarget target, IReadOnlyList<VaultEntry> all, IReadOnlyList<ScoredEntry> matches)
    {
        InitializeComponent();
        _all = all;
        _suggested = matches.Select(m => m.Entry.Id).ToHashSet();
        TargetText.Text = $"Fill into {target.Describe()}" + (matches.Count == 0 ? " — no saved match, pick any item" : "");

        Loaded += (_, _) =>
        {
            PlaceTopCenter(this, target.Hwnd);
            Refresh();
            Activate();
            SearchBox.Focus();
        };
        Deactivated += (_, _) => Finish(null);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Finish(null); } };
    }

    /// <summary>Shows the picker; completes with the chosen entry, or null if cancelled.</summary>
    public static Task<VaultEntry?> PickAsync(FillTarget target, IReadOnlyList<VaultEntry> all, IReadOnlyList<ScoredEntry> matches)
    {
        var w = new QuickPickWindow(target, all, matches);
        w.Show();
        return w._result.Task;
    }

    private void Refresh()
    {
        string q = SearchBox.Text.Trim();
        Placeholder.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        IEnumerable<VaultEntry> source = _all;
        if (q.Length > 0)
        {
            source = source.Where(e =>
                e.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Username.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Url.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        var items = source
            .Select(e => new QuickPickItem(e, _suggested.Contains(e.Id)))
            .OrderByDescending(i => i.IsSuggested)
            .ThenByDescending(i => i.Entry.IsFavorite)
            .ThenBy(i => i.Entry.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Results.ItemsSource = items;
        Results.SelectedIndex = items.Count > 0 ? 0 : -1;
        Results.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int count = Results.Items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                Results.SelectedIndex = (Results.SelectedIndex + 1) % count;
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                Results.SelectedIndex = (Results.SelectedIndex - 1 + count) % count;
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                Choose();
                break;
        }
    }

    private void Results_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Choose(); }
    }

    private void Results_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not ListBoxItem) element = VisualTreeHelper.GetParent(element);
        if (element is ListBoxItem { DataContext: QuickPickItem item })
        {
            Results.SelectedItem = item;
            Choose();
        }
    }

    private void Choose()
    {
        if (Results.SelectedItem is QuickPickItem item) Finish(item.Entry);
    }

    private void Finish(VaultEntry? entry)
    {
        if (_result.Task.IsCompleted) return;
        _result.SetResult(entry);
        Close();
    }

    /// <summary>Centers a window horizontally, a quarter of the way down the monitor holding <paramref name="anchor"/>.</summary>
    public static void PlaceTopCenter(Window window, IntPtr anchor = default)
    {
        var screen = anchor != IntPtr.Zero ? Forms.Screen.FromHandle(anchor) : Forms.Screen.FromPoint(Forms.Cursor.Position);
        var area = screen.WorkingArea;
        // Screen works in device pixels; WPF positions in DIPs.
        var source = PresentationSource.FromVisual(window);
        double scaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        double scaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
        double left = area.Left * scaleX, top = area.Top * scaleY, width = area.Width * scaleX, height = area.Height * scaleY;
        window.Left = left + (width - window.ActualWidth) / 2;
        window.Top = top + height * 0.22;
    }
}
