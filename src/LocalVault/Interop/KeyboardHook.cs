using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LocalVault.Interop;

/// <summary>
/// Global low-level keyboard hook (WH_KEYBOARD_LL). Must be installed on a thread with a message loop
/// (the WPF UI thread). The callback must return quickly, so subscribers should do minimal work.
/// Keys are never logged or persisted; the detector only looks at them while a password field is focused.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _proc; // keep delegate alive
    private IntPtr _hook;

    /// <summary>Raised on key-down with (virtual key, scan code).</summary>
    public event Action<int, int>? KeyDown;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN))
        {
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            // Ignore keystrokes LocalVault itself injects during auto-fill.
            if (data.dwExtraInfo != NativeMethods.LocalVaultInputMarker)
            try { KeyDown?.Invoke((int)data.vkCode, (int)data.scanCode); }
            catch (Exception) { /* never let an exception escape into the hook chain */ }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}

/// <summary>Out-of-context WinEvent hook for foreground-window changes.</summary>
internal sealed class ForegroundHook : IDisposable
{
    private readonly NativeMethods.WinEventDelegate _proc;
    private IntPtr _hook;

    public event Action<IntPtr>? ForegroundChanged;

    public ForegroundHook()
    {
        _proc = (_, _, hwnd, _, _, _, _) =>
        {
            try { ForegroundChanged?.Invoke(hwnd); } catch (Exception) { }
        };
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    public void Dispose() => Uninstall();
}
