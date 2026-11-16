using System.Windows;
using System.Windows.Input;
using LocalVault.Services;

namespace LocalVault.Views;

/// <summary>Compact floating master-password prompt shown when the auto-fill hotkey is used while locked.</summary>
public partial class UnlockBar : Window
{
    private readonly VaultService _vault;
    private readonly TaskCompletionSource<bool> _result = new();
    private bool _busy;

    private UnlockBar(VaultService vault, string targetDescription, IntPtr anchor)
    {
        InitializeComponent();
        _vault = vault;
        HeadingText.Text = $"Unlock LocalVault to fill {targetDescription}";
        Loaded += (_, _) =>
        {
            QuickPickWindow.PlaceTopCenter(this, anchor);
            Activate();
            MasterBox.Focus();
        };
        Deactivated += (_, _) => { if (!_busy) Finish(false); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Finish(false); } };
    }

    /// <summary>Shows the bar; completes with true once the vault is unlocked, false if cancelled.</summary>
    public static Task<bool> PromptAsync(VaultService vault, string targetDescription, IntPtr anchor)
    {
        var bar = new UnlockBar(vault, targetDescription, anchor);
        bar.Show();
        return bar._result.Task;
    }

    private async void MasterBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _busy) return;
        string master = MasterBox.Password;
        if (master.Length == 0) return;

        _busy = true;
        Busy.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        bool ok;
        try { ok = await Task.Run(() => _vault.TryUnlockCore(master)); }
        catch (Exception) { ok = false; }
        MasterBox.Clear();
        Busy.Visibility = Visibility.Collapsed;
        _busy = false;

        if (!ok)
        {
            ErrorText.Visibility = Visibility.Visible;
            MasterBox.Focus();
            return;
        }
        _vault.CompleteUnlock();
        Finish(true);
    }

    private void Finish(bool unlocked)
    {
        if (_result.Task.IsCompleted) return;
        _result.SetResult(unlocked);
        Close();
    }
}
