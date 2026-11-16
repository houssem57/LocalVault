# LocalVault

A local, offline Windows password manager (WPF, .NET 10, Fluent theme). It runs in the system tray and offers to save passwords you type into desktop apps and browsers.

## Install

```powershell
dotnet publish src/LocalVault/LocalVault.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
powershell -ExecutionPolicy Bypass -File .\CreateShortcut.ps1 -Launch   # optional: -Desktop -StartWithWindows; -Uninstall to remove
```

Installed to `%LocalAppData%\Programs\LocalVault\LocalVault.exe`, with a Start Menu shortcut. Run it with `--tray` to start minimized.

## Security model

- **Key derivation:** PBKDF2-HMAC-SHA512 with 600,000 iterations and a random 32-byte salt. This produces a 256-bit key.
- **Encryption:** each entry is a single AES-256-GCM blob with a fresh 96-bit nonce, and the entry ID is bound in as associated data. Titles, usernames and URLs are encrypted too, so nothing is stored in plaintext.
- **Storage:** SQLite at `%LocalAppData%\LocalVault\vault.db`. Settings (no secrets) are in `settings.json` next to it. Setting the `LOCALVAULT_HOME` env var moves both files somewhere else.
- **Memory:** the key exists only while the vault is unlocked and is zeroed when it locks. The vault locks after inactivity (15 min by default), when Windows locks and when the PC sleeps.
- **Clipboard:** copied secrets are kept out of Clipboard History and cloud clipboard, and are cleared after 30 seconds.
- **Network:** the app makes no network calls.

## Login detection

`PasswordDetectorService` subscribes to global UI Automation focus events. When a field with `IsPassword` gets focus in another process, it records:

- the window, the process and the browser URL (taken from the page's Document element or from the address bar)
- the username field next to it

Windows doesn't let other apps read password fields through UIA. So while that password field has focus, and only then, a `WH_KEYBOARD_LL` hook copies keystrokes into an in-memory buffer. It also handles backspace, Ctrl+A and paste. The buffer is wiped after every submit and is never logged.

Any of these counts as a submit:

- pressing Enter
- focus moving to a button or the page in the same window
- the login window closing

A submit shows the "Save password?" toast.

## Auto-fill (`Ctrl+Shift+L`)

Press the hotkey in any app or browser login form:

- **Target capture:** LocalVault notes the window you're in before it shows anything. That includes the window handle, process, title and, in browsers, the page URL read through UI Automation.
- **Locked vault:** a small unlock bar appears first.
- **Matching:**
  - Browsers: matched on domain. An exact host, a subdomain or the same registrable domain is a strong match, so `evilgithub.com` never matches `github.com`.
  - Desktop apps: matched on the executable name.
  - Matches on window title alone count as weak.
- **One strong match:** LocalVault fills it right away.
- **No strong match, or several:** a Spotlight-style picker opens, with matches first and live search across the whole vault. Use ↑/↓ and Enter, or click; Esc cancels.
- **How it fills:**
  - In a username field: types the username, presses Tab, then types the password.
  - In a password field: types only the password.
  - Optionally presses Enter afterwards.
  - Typing uses `SendInput` with `KEYEVENTF_UNICODE`, so any character works on any keyboard layout.
  - Typing stops if the target window loses focus.
  - Injected keys are tagged so LocalVault's own save detector ignores them.
- **Fallback:** if typing is rejected, or "fill by pasting" is enabled, it pastes through the clipboard and then restores your previous clipboard text.
- **Admin windows:** Windows blocks input into apps running as administrator, and LocalVault tells you when that happens.

To change the hotkey (for example, if another app already uses it), or to set auto-submit and paste mode, go to **Settings → Auto-fill**.

## Tests

```powershell
dotnet test LocalVault.slnx
```
