using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using LocalVault.Interop;
using LocalVault.Models;

namespace LocalVault.Services;

/// <summary>A username/password pair captured from another application's login form.</summary>
public sealed record CapturedCredential(
    string Title,
    string Username,
    string Password,
    string Url,
    string Host,
    string ProcessName,
    string ProcessPath,
    string WindowTitle,
    bool IsBrowser)
{
    public EntryCategory Category => IsBrowser ? EntryCategory.Website : EntryCategory.DesktopApp;

    /// <summary>Key used for "never save" rules: host for websites, process name for apps.</summary>
    public string TargetKey => IsBrowser && !string.IsNullOrEmpty(Host) ? Host : ProcessName;
}

/// <summary>
/// Watches global UI Automation focus changes. When a password field in another application is focused it
/// records the context (window, process, URL, adjacent username field) and — only while that password field
/// has focus — mirrors typed characters into an in-memory buffer. On submit (Enter, focus moving to a button /
/// page, or the login window closing) it raises <see cref="CredentialCaptured"/>. Nothing is written to disk
/// and no network calls are made; the buffer is wiped after each submit.
/// </summary>
public sealed class PasswordDetectorService : IDisposable
{

    private sealed class PasswordContext
    {
        public required AutomationElement Element { get; init; }
        public required IntPtr Hwnd { get; init; }
        public required string ProcessName { get; init; }
        public required string ProcessPath { get; init; }
        public required string WindowTitle { get; init; }
        public required string Url { get; init; }
        public required bool IsBrowser { get; init; }
        public AutomationElement? UsernameElement { get; set; }
        public string UsernameSnapshot { get; set; } = "";
        public string PreviousPassword { get; set; } = "";
        public StringBuilder Buffer { get; } = new();
        public bool SelectAll { get; set; }
        public bool HasContent => Buffer.Length > 0 || PreviousPassword.Length > 0;

        public void Wipe()
        {
            for (int i = 0; i < Buffer.Length; i++) Buffer[i] = '\0';
            Buffer.Clear();
            PreviousPassword = "";
            SelectAll = false;
        }
    }

    private readonly object _gate = new();
    private readonly Dispatcher _dispatcher;
    private readonly int _ownPid = Environment.ProcessId;
    private readonly KeyboardHook _keyboardHook = new();
    private readonly ForegroundHook _foregroundHook = new();
    private readonly Dictionary<int, (string Name, string Path)> _processCache = [];
    private readonly byte[] _keyState = new byte[256];
    private readonly StringBuilder _unicodeBuffer = new(8);

    private PasswordContext? _ctx;
    private volatile bool _passwordFocused;
    private (AutomationElement Element, IntPtr Hwnd)? _lastTextEdit;
    private (string Signature, DateTime At) _lastTrigger;
    private AutomationFocusChangedEventHandler? _focusHandler;
    private bool _running;

    public event EventHandler<CapturedCredential>? CredentialCaptured;

    public bool IsRunning => _running;

    public PasswordDetectorService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _keyboardHook.KeyDown += OnKeyDown;
        _foregroundHook.ForegroundChanged += OnForegroundChanged;
    }

    /// <summary>Must be called on the UI thread (hooks need its message loop).</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _keyboardHook.Install();
        _foregroundHook.Install();
        _focusHandler = OnFocusChanged;
        var handler = _focusHandler;
        // UIA clients should not register handlers from the UI thread.
        Task.Run(() => Automation.AddAutomationFocusChangedEventHandler(handler));
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _keyboardHook.Uninstall();
        _foregroundHook.Uninstall();
        var handler = _focusHandler;
        _focusHandler = null;
        if (handler is not null)
            Task.Run(() => { try { Automation.RemoveAutomationFocusChangedEventHandler(handler); } catch (Exception) { } });
        lock (_gate)
        {
            _ctx?.Wipe();
            _ctx = null;
            _passwordFocused = false;
            _lastTextEdit = null;
        }
    }

    // ───────────────────────────── UI Automation focus tracking ─────────────────────────────

    private void OnFocusChanged(object? sender, AutomationFocusChangedEventArgs e)
    {
        if (!_running || sender is not AutomationElement element) return;
        try
        {
            AutomationElement.AutomationElementInformation info;
            try { info = element.Current; }
            catch (ElementNotAvailableException) { return; }

            if (info.ProcessId == _ownPid)
            {
                _passwordFocused = false;
                return;
            }

            IntPtr hwnd = NativeMethods.GetForegroundWindow();

            if (info.IsPassword)
            {
                OnPasswordFieldFocused(element, info, hwnd);
                return;
            }

            PasswordContext? toTrigger = null;
            lock (_gate)
            {
                _passwordFocused = false;
                bool isTextInput = info.ControlType == ControlType.Edit || info.ControlType == ControlType.ComboBox;
                if (_ctx is { HasContent: true } ctx && ctx.Hwnd == hwnd && !isTextInput)
                    toTrigger = ctx; // e.g. focus moved to the "Sign in" button
                if (isTextInput)
                    _lastTextEdit = (element, hwnd);
            }
            if (toTrigger is not null) Trigger(toTrigger);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.COMException)
        {
            // Target app went away or refused UIA access — ignore.
        }
    }

    private void OnPasswordFieldFocused(AutomationElement element, AutomationElement.AutomationElementInformation info, IntPtr hwnd)
    {
        lock (_gate)
        {
            if (_ctx is not null && _ctx.Hwnd == hwnd && UiaHelpers.SameElement(_ctx.Element, element))
            {
                _passwordFocused = true; // refocus of the same field — keep what was typed
                return;
            }
        }

        var (processName, processPath) = GetProcessInfo(info.ProcessId);
        bool isBrowser = UiaHelpers.IsBrowser(processName);
        string url = isBrowser ? UiaHelpers.TryGetBrowserUrl(element, hwnd) : "";
        string title = NativeMethods.GetWindowTitle(hwnd);

        // Username: the text field the user was just in (same window), else the nearest preceding edit in the form.
        AutomationElement? usernameElement = null;
        string username = "";
        (AutomationElement Element, IntPtr Hwnd)? last;
        lock (_gate) last = _lastTextEdit;
        if (last is { } l && l.Hwnd == hwnd)
        {
            username = UiaHelpers.ReadValue(l.Element);
            if (!string.IsNullOrEmpty(username)) usernameElement = l.Element;
        }
        if (usernameElement is null)
            (usernameElement, username) = FindUsernameNear(element);

        var ctx = new PasswordContext
        {
            Element = element,
            Hwnd = hwnd,
            ProcessName = processName,
            ProcessPath = processPath,
            WindowTitle = title,
            Url = url,
            IsBrowser = isBrowser,
            UsernameElement = usernameElement,
            UsernameSnapshot = username,
        };

        lock (_gate)
        {
            // A second password field in the same form (e.g. "confirm password"): remember the first value.
            if (_ctx is not null && _ctx.Hwnd == hwnd && _ctx.Buffer.Length > 0)
            {
                ctx.PreviousPassword = _ctx.Buffer.ToString();
                if (ctx.UsernameElement is null) { ctx.UsernameElement = _ctx.UsernameElement; ctx.UsernameSnapshot = _ctx.UsernameSnapshot; }
            }
            _ctx?.Wipe();
            _ctx = ctx;
            _passwordFocused = true;
        }
    }

    // ───────────────────────────── Keyboard mirroring (password field only) ─────────────────────────────

    private void OnKeyDown(int vk, int scanCode)
    {
        if (!_passwordFocused) return;

        PasswordContext? toTrigger = null;
        lock (_gate)
        {
            var ctx = _ctx;
            if (ctx is null) return;
            bool ctrl = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
            bool alt = NativeMethods.IsKeyDown(NativeMethods.VK_MENU);

            switch (vk)
            {
                case NativeMethods.VK_RETURN:
                    if (ctx.HasContent) toTrigger = ctx;
                    break;
                case NativeMethods.VK_BACK:
                    if (ctrl || ctx.SelectAll) ctx.Buffer.Clear();
                    else if (ctx.Buffer.Length > 0) ctx.Buffer.Length--;
                    ctx.SelectAll = false;
                    return;
                case NativeMethods.VK_DELETE:
                    if (ctx.SelectAll) ctx.Buffer.Clear();
                    ctx.SelectAll = false;
                    return;
                case NativeMethods.VK_TAB:
                case NativeMethods.VK_ESCAPE:
                case NativeMethods.VK_LWIN:
                case NativeMethods.VK_RWIN:
                    return;
            }

            if (toTrigger is null)
            {
                if (ctrl && !alt)
                {
                    if (vk == NativeMethods.VK_A) ctx.SelectAll = true;
                    else if (vk == NativeMethods.VK_V) _dispatcher.BeginInvoke(() => AppendPaste(ctx));
                    return;
                }

                string typed = Translate(vk, scanCode, ctrl && alt);
                if (typed.Length == 0) return;
                if (ctx.SelectAll) { ctx.Buffer.Clear(); ctx.SelectAll = false; }
                foreach (char c in typed)
                    if (!char.IsControl(c)) ctx.Buffer.Append(c);
                return;
            }
        }

        // Read the form after the keystroke has been delivered, off the hook thread.
        Task.Run(() => Trigger(toTrigger));
    }

    private void AppendPaste(PasswordContext ctx)
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (Exception) { return; }
        if (text.Length == 0 || text.Length > 256) return;
        lock (_gate)
        {
            if (!ReferenceEquals(_ctx, ctx)) return;
            if (ctx.SelectAll) { ctx.Buffer.Clear(); ctx.SelectAll = false; }
            ctx.Buffer.Append(text.TrimEnd('\r', '\n'));
        }
    }

    private string Translate(int vk, int scanCode, bool altGr)
    {
        Array.Clear(_keyState);
        if (NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT)) _keyState[NativeMethods.VK_SHIFT] = 0x80;
        if ((NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0) _keyState[NativeMethods.VK_CAPITAL] = 0x01;
        if (altGr) { _keyState[NativeMethods.VK_CONTROL] = 0x80; _keyState[NativeMethods.VK_MENU] = 0x80; }

        IntPtr fg = NativeMethods.GetForegroundWindow();
        uint thread = NativeMethods.GetWindowThreadProcessId(fg, out _);
        IntPtr layout = NativeMethods.GetKeyboardLayout(thread);

        _unicodeBuffer.Clear();
        // Flag 0x4: do not change the keyboard state (keeps dead keys working in the target app).
        int n = NativeMethods.ToUnicodeEx((uint)vk, (uint)scanCode, _keyState, _unicodeBuffer, 8, 0x4, layout);
        return n > 0 ? _unicodeBuffer.ToString(0, Math.Min(n, _unicodeBuffer.Length)) : "";
    }

    // ───────────────────────────── Submit detection via window changes ─────────────────────────────

    private void OnForegroundChanged(IntPtr newForeground)
    {
        PasswordContext? ctx;
        lock (_gate) ctx = _ctx;
        if (ctx is null || !ctx.HasContent || ctx.Hwnd == newForeground) return;

        // A login dialog that closes after submit (typical for desktop apps). Check again shortly,
        // because the window may be destroyed slightly after the foreground switch.
        _ = Task.Delay(400).ContinueWith(_ =>
        {
            if (!NativeMethods.IsWindow(ctx.Hwnd) || !NativeMethods.IsWindowVisible(ctx.Hwnd))
                Trigger(ctx);
        }, TaskScheduler.Default);
    }

    // ───────────────────────────── Trigger ─────────────────────────────

    private void Trigger(PasswordContext ctx)
    {
        string password;
        lock (_gate)
        {
            if (!ctx.HasContent) return;
            password = ctx.Buffer.Length > 0 ? ctx.Buffer.ToString() : ctx.PreviousPassword;
            ctx.Wipe(); // keep the context so a retry (wrong password) is captured again
        }

        string username = ctx.UsernameElement is not null ? UiaHelpers.ReadValue(ctx.UsernameElement) : "";
        if (string.IsNullOrEmpty(username)) username = ctx.UsernameSnapshot;
        if (username.Length > 256) username = "";

        string signature = $"{ctx.Hwnd}|{username}|{password}".GetHashCode().ToString();
        lock (_gate)
        {
            if (_lastTrigger.Signature == signature && DateTime.UtcNow - _lastTrigger.At < TimeSpan.FromSeconds(15)) return;
            _lastTrigger = (signature, DateTime.UtcNow);
        }

        string host = VaultEntry.TryGetHost(ctx.Url) ?? "";
        string title = ctx.IsBrowser && host.Length > 0 ? host : SuggestAppTitle(ctx);

        var credential = new CapturedCredential(title, username, password, ctx.Url, host, ctx.ProcessName,
            ctx.ProcessPath, ctx.WindowTitle, ctx.IsBrowser);
        _dispatcher.BeginInvoke(() => CredentialCaptured?.Invoke(this, credential));
    }

    private static string SuggestAppTitle(PasswordContext ctx)
    {
        try
        {
            if (!string.IsNullOrEmpty(ctx.ProcessPath))
            {
                var info = FileVersionInfo.GetVersionInfo(ctx.ProcessPath);
                // FileDescription is usually the friendly app name ("Steam", "Discord"); ProductName is often
                // generic for system-hosted exes ("Microsoft® Windows® Operating System").
                foreach (string? candidate in new[] { info.FileDescription, info.ProductName })
                {
                    string name = candidate?.Trim() ?? "";
                    if (name.Length is > 0 and <= 60 && !name.Contains("Operating System", StringComparison.OrdinalIgnoreCase))
                        return name;
                }
            }
        }
        catch (Exception) { }
        if (!string.IsNullOrWhiteSpace(ctx.WindowTitle) && ctx.WindowTitle.Length <= 60) return ctx.WindowTitle;
        return ctx.ProcessName;
    }

    // ───────────────────────────── UIA helpers ─────────────────────────────

    private static (AutomationElement? Element, string Value) FindUsernameNear(AutomationElement passwordElement)
    {
        var walker = TreeWalker.ControlViewWalker;
        var editCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
        try
        {
            AutomationElement? container = passwordElement;
            for (int level = 0; level < 4 && container is not null; level++)
            {
                container = walker.GetParent(container);
                if (container is null || container == AutomationElement.RootElement) break;

                var edits = container.FindAll(TreeScope.Descendants, editCondition);
                if (edits.Count > 60) break; // too broad — we've left the form
                AutomationElement? candidate = null;
                string candidateValue = "";
                foreach (AutomationElement edit in edits)
                {
                    if (UiaHelpers.SameElement(edit, passwordElement)) break;
                    string v = UiaHelpers.ReadValue(edit);
                    if (v.Length > 0) { candidate = edit; candidateValue = v; }
                }
                if (candidate is not null) return (candidate, candidateValue);
            }
        }
        catch (Exception) { }
        return (null, "");
    }

    private (string Name, string Path) GetProcessInfo(int pid)
    {
        lock (_processCache)
        {
            if (_processCache.TryGetValue(pid, out var cached)) return cached;
        }
        string name = "", path = "";
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
            try { path = p.MainModule?.FileName ?? ""; } catch (Exception) { }
        }
        catch (Exception) { }
        lock (_processCache)
        {
            if (_processCache.Count > 200) _processCache.Clear();
            _processCache[pid] = (name, path);
        }
        return (name, path);
    }

    public void Dispose()
    {
        Stop();
        _keyboardHook.Dispose();
        _foregroundHook.Dispose();
    }
}
