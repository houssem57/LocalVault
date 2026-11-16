using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalVault.ViewModels;

namespace LocalVault.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel; // assigned before InitializeComponent: SelectionChanged fires while XAML loads
        InitializeComponent();
        DataContext = viewModel;

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsUnlocked) && !_vm.IsUnlocked)
                Dispatcher.BeginInvoke(FocusLockScreen);
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(FocusLockScreen); };

        InputBindings.Add(new KeyBinding(new RelayKeyCommand(() => { SearchBox.Focus(); SearchBox.SelectAll(); }), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_vm.AddCommand, Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_vm.EditCommand, Key.E, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_vm.LockCommand, Key.L, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_vm.CopyPasswordCommand, Key.C, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(_vm.CopyUsernameCommand, Key.B, ModifierKeys.Control));
    }

    private void FocusLockScreen()
    {
        if (_vm.IsUnlocked) return;
        if (_vm.IsInitialized) UnlockPassword.Focus(); else CreatePassword.Focus();
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        bool ok = await _vm.UnlockAsync(UnlockPassword.Password);
        UnlockPassword.Clear();
        if (!ok) UnlockPassword.Focus();
    }

    private void UnlockPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Unlock_Click(sender, e);
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        bool ok = await _vm.CreateVaultAsync(CreatePassword.Password, ConfirmPassword.Password);
        if (ok)
        {
            CreatePassword.Clear();
            ConfirmPassword.Clear();
        }
    }

    private void ConfirmPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Create_Click(sender, e);
    }

    private void CreatePassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        var (score, label) = PasswordStrength.Evaluate(CreatePassword.Password);
        StrengthBar.Value = score;
        StrengthText.Text = label;
    }

    private void FilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterList.SelectedItem is ListBoxItem { Tag: VaultFilter filter })
            _vm.Filter = filter;
    }

    private void EntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.CopyPasswordCommand.CanExecute(null)) _vm.CopyPasswordCommand.Execute(null);
    }

    private sealed class RelayKeyCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}

internal static class PasswordStrength
{
    public static (double Score, string Label) Evaluate(string password)
    {
        if (string.IsNullOrEmpty(password)) return (0, "");
        int pool = 0;
        if (password.Any(char.IsLower)) pool += 26;
        if (password.Any(char.IsUpper)) pool += 26;
        if (password.Any(char.IsDigit)) pool += 10;
        if (password.Any(c => !char.IsLetterOrDigit(c))) pool += 32;
        double bits = password.Length * Math.Log2(Math.Max(pool, 1));
        string label = bits switch
        {
            < 40 => "Weak",
            < 60 => "Fair",
            < 80 => "Good",
            _ => "Strong",
        };
        if (password.Length < 8) label = "Too short (minimum 8 characters)";
        return (Math.Min(100, bits / 100 * 100), label + $" · ~{bits:0} bits");
    }
}
