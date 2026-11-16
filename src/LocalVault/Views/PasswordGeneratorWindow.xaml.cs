using System.Windows;
using LocalVault.Services;

namespace LocalVault.Views;

public partial class PasswordGeneratorWindow : Window
{
    private static PasswordGeneratorWindow? _toolInstance;
    private readonly ClipboardService? _clipboard;
    private readonly SettingsService? _settings;
    private bool _ready;

    public string? Result { get; private set; }

    private PasswordGeneratorWindow(bool pickMode, ClipboardService? clipboard, SettingsService? settings)
    {
        InitializeComponent();
        _clipboard = clipboard;
        _settings = settings;
        UseButton.Visibility = pickMode ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Content = pickMode ? "Cancel" : "Close";
        _ready = true;
        Regenerate();
    }

    /// <summary>Opens the generator as a standalone tool (single instance, non-modal).</summary>
    public static void ShowTool(Window? owner, ClipboardService clipboard, SettingsService settings)
    {
        if (_toolInstance is { IsLoaded: true })
        {
            _toolInstance.Activate();
            return;
        }
        _toolInstance = new PasswordGeneratorWindow(false, clipboard, settings);
        if (owner is not null) _toolInstance.Owner = owner; else _toolInstance.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _toolInstance.Closed += (_, _) => _toolInstance = null;
        _toolInstance.Show();
        _toolInstance.Activate();
    }

    /// <summary>Opens the generator modally and returns the chosen password (or null).</summary>
    public static string? Pick(Window owner)
    {
        var w = new PasswordGeneratorWindow(true, null, null) { Owner = owner };
        return w.ShowDialog() == true ? w.Result : null;
    }

    private PasswordOptions Options => new(
        (int)LengthSlider.Value,
        UpperBox.IsChecked == true,
        LowerBox.IsChecked == true,
        DigitBox.IsChecked == true,
        SymbolBox.IsChecked == true);

    private void Regenerate()
    {
        if (!_ready) return;
        var options = Options;
        if (!options.Uppercase && !options.Lowercase && !options.Digits && !options.Symbols)
        {
            LowerBox.IsChecked = true; // at least one set is required
            return;
        }
        Output.Text = PasswordGenerator.Generate(options);
        EntropyText.Text = $"≈ {PasswordGenerator.EstimateEntropyBits(options):0} bits of entropy";
        CopiedText.Visibility = Visibility.Collapsed;
    }

    private void Option_Changed(object sender, RoutedEventArgs e) => Regenerate();

    private void Regenerate_Click(object sender, RoutedEventArgs e) => Regenerate();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        int seconds = _settings?.Current.ClipboardClearSeconds ?? 30;
        if (_clipboard is not null) _clipboard.CopySecret(Output.Text, TimeSpan.FromSeconds(seconds));
        else new ClipboardService().CopySecret(Output.Text, TimeSpan.FromSeconds(seconds));
        CopiedText.Visibility = Visibility.Visible;
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        Result = Output.Text;
        DialogResult = true;
    }
}
