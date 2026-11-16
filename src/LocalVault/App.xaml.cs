using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using LocalVault.Interop;
using LocalVault.Models;
using LocalVault.Services;
using LocalVault.ViewModels;
using LocalVault.Views;

namespace LocalVault;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showSignalRegistration;

    private VaultService _vault = null!;
    private SettingsService _settings = null!;
    private ClipboardService _clipboard = null!;
    private AutoLockService? _autoLock;
    private TrayService? _tray;
    private PasswordDetectorService? _detector;
    private MainViewModel _viewModel = null!;
    private MainWindow? _mainWindow;
    private SaveCredentialToast? _toast;
    private GlobalHotkeyService? _hotkeys;
    private AutoFillService? _autoFill;
    private bool _fillInProgress;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string sid = WindowsIdentity.GetCurrent().User?.Value ?? "user";
        _singleInstance = new Mutex(initiallyOwned: true, $@"Local\LocalVault.Instance.{sid}", out bool createdNew);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\LocalVault.Show.{sid}");
        bool startInTray = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        if (!createdNew)
        {
            if (!startInTray) _showSignal.Set(); // bring the running instance to the front
            Shutdown();
            return;
        }
        _showSignalRegistration = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, Timeout.Infinite, executeOnlyOnce: false);

        DispatcherUnhandledException += OnUnhandledException;

        _settings = new SettingsService();
        _vault = new VaultService(new VaultStore(VaultStore.DefaultPath));
        _clipboard = new ClipboardService();
        _autoLock = new AutoLockService(_vault, _settings);
        _viewModel = new MainViewModel(_vault, _clipboard, _settings);

        try { StartupService.RefreshPathIfEnabled(); } catch (Exception) { }

        _mainWindow = new MainWindow(_viewModel);
        _mainWindow.Closing += (_, args) =>
        {
            if (_exiting) return;
            args.Cancel = true; // closing hides to tray
            _mainWindow.Hide();
            if (!_settings.Current.TrayHintShown)
            {
                _tray?.ShowBalloon("LocalVault is still running", "It keeps watching for logins in the tray. Right-click the icon to exit.");
                _settings.Current.TrayHintShown = true;
                _settings.Save();
            }
        };
        MainWindow = _mainWindow;

        _tray = new TrayService();
        _tray.OpenRequested += (_, _) => ShowMainWindow();
        _tray.LockRequested += (_, _) => _vault.Lock();
        _tray.GeneratorRequested += (_, _) => PasswordGeneratorWindow.ShowTool(null, _clipboard, _settings);
        _tray.ExitRequested += (_, _) => ExitApp();
        _tray.DetectionToggled += (_, enabled) => SetDetection(enabled);
        _tray.SetLocked(true);
        _vault.Locked += (_, _) => _tray.SetLocked(true);
        _vault.Unlocked += (_, _) => _tray.SetLocked(false);

        _detector = new PasswordDetectorService(Dispatcher);
        _detector.CredentialCaptured += OnCredentialCaptured;
        _tray.SetDetection(_settings.Current.DetectionEnabled); // raises DetectionToggled → starts detector
        if (_settings.Current.DetectionEnabled && !_detector.IsRunning) SetDetection(true);

        SettingsWindow.DetectionChanged += enabled => _tray.SetDetection(enabled);

        _autoFill = new AutoFillService(Dispatcher);
        _hotkeys = new GlobalHotkeyService();
        _hotkeys.Pressed += OnAutoFillHotkey;
        var hotkey = Hotkey.TryParse(_settings.Current.AutoFillHotkey, out var parsed) ? parsed : Hotkey.Default;
        if (!_hotkeys.Register(hotkey))
            _tray.ShowBalloon("Auto-fill hotkey unavailable",
                $"{hotkey} is already used by another app. Pick a different one in LocalVault → Settings.");
        SettingsWindow.ApplyHotkey = TryApplyHotkey;
        SettingsWindow.SuspendHotkey = suspend =>
        {
            if (suspend) _hotkeys.Unregister();
            else if (_hotkeys.Current is null && Hotkey.TryParse(_settings.Current.AutoFillHotkey, out var saved)) _hotkeys.Register(saved);
        };

        if (!startInTray) ShowMainWindow();
    }

    private void SetDetection(bool enabled)
    {
        if (_detector is null) return;
        try
        {
            if (enabled) _detector.Start(); else _detector.Stop();
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloon("LocalVault", "Could not start login detection: " + ex.Message);
            enabled = false;
        }
        if (_settings.Current.DetectionEnabled != enabled)
        {
            _settings.Current.DetectionEnabled = enabled;
            _settings.Save();
        }
    }

    private void OnCredentialCaptured(object? sender, CapturedCredential credential)
    {
        if (!_settings.Current.DetectionEnabled) return;
        if (string.IsNullOrEmpty(credential.Password)) return;
        if (_settings.IsNeverSave(credential.TargetKey)) return;

        if (_vault.IsUnlocked)
        {
            var existing = _vault.FindMatch(credential.Host, credential.ProcessName, credential.Username);
            if (existing is not null && existing.Password == credential.Password) return; // already saved
        }

        _toast?.Close();
        _toast = new SaveCredentialToast(credential, _vault, _settings);
        _toast.Closed += (s, _) => { if (ReferenceEquals(_toast, s)) _toast = null; };
        _toast.Show();
    }

    private bool TryApplyHotkey(Hotkey hotkey)
    {
        if (_hotkeys is null) return false;
        if (_hotkeys.Current == hotkey) return true;
        if (_hotkeys.Register(hotkey)) return true;
        // Conflict: put the previous hotkey back.
        if (Hotkey.TryParse(_settings.Current.AutoFillHotkey, out var previous)) _hotkeys.Register(previous);
        return false;
    }

    /// <summary>Global hotkey: fill credentials into whatever window currently has focus.</summary>
    private async void OnAutoFillHotkey(object? sender, EventArgs e)
    {
        if (_fillInProgress || _autoFill is null) return;
        // Capture the target before any LocalVault UI can take the foreground.
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (hwnd == IntPtr.Zero || pid == (uint)Environment.ProcessId) return;

        _fillInProgress = true;
        try
        {
            if (!_vault.IsInitialized)
            {
                ShowMainWindow();
                return;
            }

            var target = await Task.Run(() => AutoFillService.CaptureTarget(hwnd));

            if (!_vault.IsUnlocked && !await UnlockBar.PromptAsync(_vault, target.Describe(), hwnd))
                return;

            var entries = _vault.Entries.ToList();
            var ranked = CredentialMatcher.Rank(target, entries);
            var strong = ranked.Where(r => r.IsStrong).ToList();

            VaultEntry? entry = strong.Count == 1
                ? strong[0].Entry
                : await QuickPickWindow.PickAsync(target, entries, ranked);
            if (entry is null) return;

            _autoLock?.Touch();
            var result = await _autoFill.FillAsync(target, entry,
                new FillOptions(_settings.Current.AutoSubmit, _settings.Current.AlwaysPaste));
            switch (result)
            {
                case FillResult.TargetElevated:
                    _tray?.ShowBalloon("Can't fill this window",
                        $"{target.ProcessName}.exe runs as administrator, and Windows blocks input from normal apps into it.");
                    break;
                case FillResult.FocusLost:
                    _tray?.ShowBalloon("Auto-fill stopped", "The target window lost focus, so nothing more was typed.");
                    break;
                case FillResult.TargetGone:
                    _tray?.ShowBalloon("Auto-fill cancelled", "The target window was closed.");
                    break;
            }
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloon("Auto-fill failed", ex.Message);
        }
        finally
        {
            _fillInProgress = false;
        }
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;  // reliably bring to front
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private void ExitApp()
    {
        _exiting = true;
        _vault.Lock();
        Shutdown();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            // Diagnostics only: stack traces never contain vault contents.
            string log = Path.Combine(Path.GetDirectoryName(VaultStore.DefaultPath)!, "error.log");
            File.AppendAllText(log, $"[{DateTimeOffset.Now:O}] {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception) { }
        MessageBox.Show(e.Exception.Message,"LocalVault — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _clipboard?.ClearIfUnchanged();
        _hotkeys?.Dispose();
        _detector?.Dispose();
        _autoLock?.Dispose();
        _tray?.Dispose();
        _showSignalRegistration?.Unregister(null);
        _showSignal?.Dispose();
        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); } catch (ApplicationException) { }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}
