using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LocalVault.Services;

/// <summary>Locks the vault after a period without interaction with LocalVault, and whenever Windows locks or sleeps.</summary>
public sealed class AutoLockService : IDisposable
{
    private readonly VaultService _vault;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher; // constructed on the UI thread
    private DateTime _lastActivity = DateTime.UtcNow;

    public AutoLockService(VaultService vault, SettingsService settings)
    {
        _vault = vault;
        _settings = settings;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();

        InputManager.Current.PreNotifyInput += OnInput;
        _vault.Unlocked += (_, _) => Touch();
        _vault.EntriesChanged += (_, _) => Touch();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void Touch() => _lastActivity = DateTime.UtcNow;

    private void OnInput(object sender, NotifyInputEventArgs e)
    {
        if (e.StagingItem.Input is KeyboardEventArgs or MouseButtonEventArgs or MouseWheelEventArgs)
            Touch();
    }

    private void Check()
    {
        if (!_vault.IsUnlocked) return;
        if (DateTime.UtcNow - _lastActivity >= TimeSpan.FromMinutes(_settings.Current.AutoLockMinutes))
            _vault.Lock();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            _dispatcher.BeginInvoke(_vault.Lock);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            _dispatcher.BeginInvoke(_vault.Lock);
    }

    public void Dispose()
    {
        _timer.Stop();
        InputManager.Current.PreNotifyInput -= OnInput;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
