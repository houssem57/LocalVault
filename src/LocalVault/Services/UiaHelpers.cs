using System.Windows.Automation;

namespace LocalVault.Services;

/// <summary>Shared UI Automation helpers used by login detection and auto-fill.</summary>
public static class UiaHelpers
{
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "chromium",
        "librewolf", "waterfox", "iexplore", "thorium", "yandex", "zen", "floorp",
    };

    public static bool IsBrowser(string processName) => BrowserProcesses.Contains(processName);

    public static bool SameElement(AutomationElement a, AutomationElement b)
    {
        try { return Automation.Compare(a, b); }
        catch (Exception) { return false; }
    }

    public static string ReadValue(AutomationElement element)
    {
        try
        {
            if (element.Current.IsPassword) return "";
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern))
                return ((ValuePattern)pattern).Current.Value?.Trim() ?? "";
        }
        catch (Exception) { }
        return "";
    }

    public static string TryGetBrowserUrl(AutomationElement? startElement, IntPtr hwnd)
    {
        // 1) Chromium & Firefox expose the page URL as the Value of the web-content Document element.
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            AutomationElement? node = startElement;
            for (int i = 0; i < 40 && node is not null; i++)
            {
                if (node.Current.ControlType == ControlType.Document)
                {
                    if (node.TryGetCurrentPattern(ValuePattern.Pattern, out object p))
                    {
                        string v = ((ValuePattern)p).Current.Value ?? "";
                        if (LooksLikeUrl(v)) return v;
                    }
                }
                node = walker.GetParent(node);
            }
        }
        catch (Exception) { }

        // 2) Fall back to the browser's address bar (outside the page content).
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<(AutomationElement El, int Depth)>();
            queue.Enqueue((root, 0));
            AutomationElement? firstEdit = null;
            int visited = 0;
            while (queue.Count > 0 && visited++ < 500)
            {
                var (el, depth) = queue.Dequeue();
                var info = el.Current;
                if (info.ControlType == ControlType.Document) continue; // skip page content
                if (info.ControlType == ControlType.Edit)
                {
                    string name = info.Name ?? "";
                    if (info.AutomationId == "urlbar-input" ||
                        name.Contains("address", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("url", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = ReadValue(el);
                        if (v.Length > 0) return v;
                    }
                    firstEdit ??= el;
                }
                if (depth >= 14) continue;
                for (var child = walker.GetFirstChild(el); child is not null; child = walker.GetNextSibling(child))
                    queue.Enqueue((child, depth + 1));
            }
            if (firstEdit is not null)
            {
                string v = ReadValue(firstEdit);
                if (LooksLikeUrl(v)) return v;
            }
        }
        catch (Exception) { }
        return "";
    }

    public static bool LooksLikeUrl(string v) =>
        !string.IsNullOrWhiteSpace(v) && !v.Contains(' ') && v.Contains('.') &&
        !v.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) &&
        !v.StartsWith("about:", StringComparison.OrdinalIgnoreCase);

}
