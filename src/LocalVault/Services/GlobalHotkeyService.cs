using System.Windows.Input;
using System.Windows.Interop;
using LocalVault.Interop;

namespace LocalVault.Services;

/// <summary>A modifier + key combination, e.g. "Ctrl+Shift+L".</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public static readonly Hotkey Default = new(ModifierKeys.Control | ModifierKeys.Shift, Key.L);

    public uint NativeModifiers =>
        (Modifiers.HasFlag(ModifierKeys.Control) ? NativeMethods.MOD_CONTROL : 0) |
        (Modifiers.HasFlag(ModifierKeys.Shift) ? NativeMethods.MOD_SHIFT : 0) |
        (Modifiers.HasFlag(ModifierKeys.Alt) ? NativeMethods.MOD_ALT : 0) |
        (Modifiers.HasFlag(ModifierKeys.Windows) ? NativeMethods.MOD_WIN : 0);

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    /// <summary>Needs Ctrl, Alt or Win (Shift alone would swallow normal typing) and a non-modifier key.</summary>
    public bool IsValid =>
        (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0 &&
        Key is not (Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt
            or Key.RightAlt or Key.LWin or Key.RWin or Key.System);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        _ => key.ToString(),
    };

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = ModifierKeys.None;
        Key key = Key.None;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "win" or "windows": mods |= ModifierKeys.Windows; break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    else if (!Enum.TryParse(raw, ignoreCase: true, out key)) return false;
                    break;
            }
        }
        hotkey = new Hotkey(mods, key);
        return hotkey.IsValid;
    }
}

/// <summary>
/// Registers a system-wide hotkey with RegisterHotKey on a hidden message-only window (HwndSource
/// parented to HWND_MESSAGE). Must be created and used on the UI thread.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0x4C56; // "LV"
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly HwndSource _source;
    private bool _registered;

    public event EventHandler? Pressed;

    public Hotkey? Current { get; private set; }

    public GlobalHotkeyService()
    {
        var parameters = new HwndSourceParameters("LocalVault.HotkeySink")
        {
            ParentWindow = HWND_MESSAGE,
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers (replacing any previous hotkey). Returns false if another app already owns it.</summary>
    public bool Register(Hotkey hotkey)
    {
        Unregister();
        if (!hotkey.IsValid) return false;
        _registered = NativeMethods.RegisterHotKey(_source.Handle, HotkeyId,
            hotkey.NativeModifiers | NativeMethods.MOD_NOREPEAT, hotkey.VirtualKey);
        Current = _registered ? hotkey : null;
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
        Current = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
