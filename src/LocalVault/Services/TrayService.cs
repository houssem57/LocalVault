using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace LocalVault.Services;

/// <summary>System tray icon and context menu (WinForms NotifyIcon hosted inside the WPF app).</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _lockItem;
    private readonly Forms.ToolStripMenuItem _detectItem;
    private readonly Forms.ToolStripMenuItem _startupItem;

    public event EventHandler? OpenRequested;
    public event EventHandler? LockRequested;
    public event EventHandler? GeneratorRequested;
    public event EventHandler<bool>? DetectionToggled;
    public event EventHandler? ExitRequested;

    public TrayService()
    {
        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("Open LocalVault", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty))
        {
            Font = new Drawing.Font(Forms.SystemInformation.MenuFont, Drawing.FontStyle.Bold),
        };
        _lockItem = new Forms.ToolStripMenuItem("Lock vault", null, (_, _) => LockRequested?.Invoke(this, EventArgs.Empty));
        var generator = new Forms.ToolStripMenuItem("Password generator…", null, (_, _) => GeneratorRequested?.Invoke(this, EventArgs.Empty));
        _detectItem = new Forms.ToolStripMenuItem("Offer to save passwords") { CheckOnClick = true };
        _detectItem.CheckedChanged += (_, _) => DetectionToggled?.Invoke(this, _detectItem.Checked);
        _startupItem = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        _startupItem.Click += (_, _) => StartupService.SetEnabled(_startupItem.Checked);
        var exit = new Forms.ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        menu.Items.AddRange([open, _lockItem, generator, new Forms.ToolStripSeparator(), _detectItem, _startupItem,
            new Forms.ToolStripSeparator(), exit]);
        menu.Opening += (_, _) => _startupItem.Checked = StartupService.IsEnabled;

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "LocalVault",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public void SetLocked(bool locked)
    {
        _lockItem.Enabled = !locked;
        _icon.Text = locked ? "LocalVault (locked)" : "LocalVault (unlocked)";
    }

    public void SetDetection(bool enabled)
    {
        if (_detectItem.Checked != enabled) _detectItem.Checked = enabled;
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.None);

    private static Drawing.Icon LoadIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        if (info is null) return Drawing.SystemIcons.Shield;
        using var stream = info.Stream;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
