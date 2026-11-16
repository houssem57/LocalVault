using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace LocalVault.Services;

/// <summary>Copies secrets to the clipboard, excludes them from Clipboard History / cloud sync, and clears them after a timeout.</summary>
public sealed class ClipboardService
{
    private readonly DispatcherTimer _timer = new();
    private string? _lastCopied;

    public event EventHandler? Cleared;

    public ClipboardService()
    {
        _timer.Tick += (_, _) => ClearIfUnchanged();
    }

    public void CopySecret(string text, TimeSpan clearAfter)
    {
        if (string.IsNullOrEmpty(text)) return;
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        // Ask Windows not to record this in Clipboard History (Win+V) or sync it to the cloud.
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 0, 0, 0, 0 }));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));

        if (!TrySet(() => Clipboard.SetDataObject(data, copy: true))) return;

        _lastCopied = text;
        _timer.Stop();
        _timer.Interval = clearAfter;
        _timer.Start();
    }

    public void ClearIfUnchanged()
    {
        _timer.Stop();
        if (_lastCopied is null) return;
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == _lastCopied)
                TrySet(Clipboard.Clear);
        }
        catch (Exception)
        {
            // Clipboard can be momentarily locked by another process; nothing else to do.
        }
        _lastCopied = null;
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    private static bool TrySet(Action action)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { action(); return true; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(30); }
        }
        return false;
    }
}
