using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using LocalVault.Interop;
using LocalVault.Models;

namespace LocalVault.Services;

public sealed record FillOptions(bool AutoSubmit, bool AlwaysPaste);

public enum FillResult
{
    Typed,
    Pasted,
    TargetGone,
    TargetElevated,
    FocusLost,
}

/// <summary>
/// Types credentials into another application's focused field with SendInput (KEYEVENTF_UNICODE), so any
/// character works regardless of keyboard layout. Falls back to a clipboard paste (restoring the previous
/// clipboard text) if the target rejects synthesized input. Injected events carry a marker so LocalVault's own
/// login detector ignores them. Typing aborts if the target window loses the foreground, so nothing is ever
/// typed into the wrong window.
/// </summary>
public sealed class AutoFillService
{
    private static readonly int InputSize = Marshal.SizeOf<NativeMethods.INPUT>();
    private readonly Dispatcher _dispatcher;

    public AutoFillService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Describes the window under the hotkey. Blocking UIA work — call off the UI thread.</summary>
    public static FillTarget CaptureTarget(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        string name = "", path = "";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
            try { path = p.MainModule?.FileName ?? ""; } catch (Exception) { }
        }
        catch (Exception) { }

        bool isBrowser = UiaHelpers.IsBrowser(name);
        string url = "";
        if (isBrowser)
        {
            AutomationElement? focused = null;
            try { focused = AutomationElement.FocusedElement; } catch (Exception) { }
            url = UiaHelpers.TryGetBrowserUrl(focused, hwnd);
        }
        return new FillTarget(hwnd, pid, name, path, NativeMethods.GetWindowTitle(hwnd), url, isBrowser);
    }

    public async Task<FillResult> FillAsync(FillTarget target, VaultEntry entry, FillOptions options)
    {
        if (!NativeMethods.IsWindow(target.Hwnd)) return FillResult.TargetGone;
        // Windows (UIPI) silently drops input sent from a normal process into an elevated one.
        if (NativeMethods.IsProcessElevated(target.ProcessId) && !NativeMethods.IsProcessElevated((uint)Environment.ProcessId))
            return FillResult.TargetElevated;

        await WaitForModifiersReleasedAsync();
        if (!await BringToFrontAsync(target.Hwnd)) return FillResult.FocusLost;
        await Task.Delay(100);

        bool focusedIsPassword = await Task.Run(() =>
        {
            try { return AutomationElement.FocusedElement?.Current.IsPassword == true; }
            catch (Exception) { return false; }
        });

        bool usedPaste = false;
        if (!focusedIsPassword && entry.Username.Length > 0)
        {
            if (!IsForeground(target.Hwnd)) return FillResult.FocusLost;
            usedPaste |= await SendTextAsync(entry.Username, options.AlwaysPaste);
            await Task.Run(() => SendKey(0x09 /* VK_TAB */));
            await Task.Delay(150);
        }

        if (!IsForeground(target.Hwnd)) return FillResult.FocusLost;
        usedPaste |= await SendTextAsync(entry.Password, options.AlwaysPaste);

        if (options.AutoSubmit)
        {
            await Task.Delay(80);
            if (!IsForeground(target.Hwnd)) return FillResult.FocusLost;
            await Task.Run(() => SendKey(NativeMethods.VK_RETURN));
        }
        return usedPaste ? FillResult.Pasted : FillResult.Typed;
    }

    /// <summary>Returns true if the clipboard fallback was used.</summary>
    private async Task<bool> SendTextAsync(string text, bool alwaysPaste)
    {
        if (text.Length == 0) return false;
        if (!alwaysPaste && await Task.Run(() => TypeUnicode(text))) return false;
        await PasteAsync(text);
        return true;
    }

    private static bool TypeUnicode(string text)
    {
        foreach (char c in text) // UTF-16 code units; surrogate pairs are sent as two units, which Windows recombines
        {
            if (c is '\r' or '\n') continue;
            var inputs = new[] { UnicodeInput(c, keyUp: false), UnicodeInput(c, keyUp: true) };
            if (NativeMethods.SendInput((uint)inputs.Length, inputs, InputSize) != inputs.Length) return false;
            Thread.Sleep(3); // some web/Electron fields drop characters that arrive in a single burst
        }
        return true;
    }

    private async Task PasteAsync(string text)
    {
        // Clipboard needs the STA UI thread.
        string? previous = await _dispatcher.InvokeAsync(() =>
        {
            string? old = null;
            try { if (Clipboard.ContainsText()) old = Clipboard.GetText(); } catch (Exception) { }
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 0, 0, 0, 0 }));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            try { Clipboard.SetDataObject(data, copy: true); } catch (Exception) { }
            return old;
        });

        await Task.Run(() => SendChord(NativeMethods.VK_CONTROL, NativeMethods.VK_V));
        await Task.Delay(250); // let the target read the clipboard before restoring it

        await _dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (previous is not null) Clipboard.SetText(previous);
                else Clipboard.Clear();
            }
            catch (Exception) { }
        });
    }

    private static NativeMethods.INPUT UnicodeInput(char c, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wScan = c,
                dwFlags = NativeMethods.KEYEVENTF_UNICODE | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0),
                dwExtraInfo = NativeMethods.LocalVaultInputMarker,
            },
        },
    };

    private static NativeMethods.INPUT VkInput(int vk, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = (ushort)vk,
                dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
                dwExtraInfo = NativeMethods.LocalVaultInputMarker,
            },
        },
    };

    private static void SendKey(int vk)
    {
        var inputs = new[] { VkInput(vk, false), VkInput(vk, true) };
        NativeMethods.SendInput((uint)inputs.Length, inputs, InputSize);
    }

    private static void SendChord(int modifier, int vk)
    {
        var inputs = new[] { VkInput(modifier, false), VkInput(vk, false), VkInput(vk, true), VkInput(modifier, true) };
        NativeMethods.SendInput((uint)inputs.Length, inputs, InputSize);
    }

    /// <summary>The hotkey's Ctrl/Shift may still be held; typing now would turn letters into shortcuts.</summary>
    private static async Task WaitForModifiersReleasedAsync()
    {
        int[] modifiers = [NativeMethods.VK_CONTROL, NativeMethods.VK_SHIFT, NativeMethods.VK_MENU, NativeMethods.VK_LWIN, NativeMethods.VK_RWIN];
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && modifiers.Any(NativeMethods.IsKeyDown))
            await Task.Delay(20);
        await Task.Delay(30);
    }

    private static bool IsForeground(IntPtr hwnd)
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        return fg == hwnd || NativeMethods.GetAncestor(fg, NativeMethods.GA_ROOT) == NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
    }

    private static async Task<bool> BringToFrontAsync(IntPtr hwnd)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (IsForeground(hwnd)) return true;
            if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

            // Attaching to the current foreground thread's input lets SetForegroundWindow succeed reliably.
            IntPtr fg = NativeMethods.GetForegroundWindow();
            uint fgThread = NativeMethods.GetWindowThreadProcessId(fg, out _);
            uint myThread = NativeMethods.GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != myThread && NativeMethods.AttachThreadInput(myThread, fgThread, true);
            try { NativeMethods.SetForegroundWindow(hwnd); }
            finally { if (attached) NativeMethods.AttachThreadInput(myThread, fgThread, false); }

            await Task.Delay(60);
        }
        return IsForeground(hwnd);
    }
}
