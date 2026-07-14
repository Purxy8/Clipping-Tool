using System.Drawing;
using Forms = System.Windows.Forms;

namespace ClipForge.Services;

/// <summary>
/// Keeps ClipForge reachable while the main window is hidden. The process must
/// remain alive for replay capture and global hotkeys to continue working.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _saveItem;
    private readonly Icon? _icon;
    private bool _disposed;

    public TrayIconService()
    {
        _icon = TryLoadApplicationIcon();
        _saveItem = new Forms.ToolStripMenuItem("Save replay clip", null, (_, _) => SaveClipRequested?.Invoke(this, EventArgs.Empty))
        {
            Enabled = false
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Open ClipForge", null, (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(_saveItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit ClipForge", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "ClipForge — Replay off",
            Icon = _icon ?? SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;
    }

    public event EventHandler? ShowRequested;

    public event EventHandler? SaveClipRequested;

    public event EventHandler? ExitRequested;

    public void UpdateStatus(string status, bool canSave)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var text = $"ClipForge — {status}";
        _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
        _saveItem.Enabled = canSave;
    }

    public void ShowBackgroundHint()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _notifyIcon.BalloonTipTitle = "ClipForge is still running";
        _notifyIcon.BalloonTipText = "Replay and shortcuts continue in the background. Double-click the tray icon to reopen ClipForge.";
        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(3500);
    }

    public void ShowReplayStartupFailure(string detail)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var safeDetail = string.IsNullOrWhiteSpace(detail)
            ? "Open ClipForge to review the saved capture settings and try again."
            : detail.Trim();
        if (safeDetail.Length > 220)
        {
            safeDetail = $"{safeDetail[..217]}...";
        }

        _notifyIcon.BalloonTipTitle = "Automatic replay did not start";
        _notifyIcon.BalloonTipText = safeDetail;
        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Warning;
        _notifyIcon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.DoubleClick -= NotifyIcon_DoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon?.Dispose();
    }

    private void NotifyIcon_DoubleClick(object? sender, EventArgs e) =>
        ShowRequested?.Invoke(this, EventArgs.Empty);

    private static Icon? TryLoadApplicationIcon()
    {
        try
        {
            return Environment.ProcessPath is { Length: > 0 } processPath
                ? Icon.ExtractAssociatedIcon(processPath)
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return null;
        }
    }
}
