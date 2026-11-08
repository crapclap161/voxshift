using System;
using System.Drawing;
using WinForms = System.Windows.Forms;

namespace VoxShift.Services;

/// <summary>System tray icon with quick mode switching.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _micItem;
    private readonly WinForms.ToolStripMenuItem _appItem;

    public TrayIcon()
    {
        _micItem = new WinForms.ToolStripMenuItem("Microphone", null, (_, _) => MicrophoneRequested?.Invoke());
        _appItem = new WinForms.ToolStripMenuItem("Application audio", null, (_, _) => ApplicationRequested?.Invoke());
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(new WinForms.ToolStripMenuItem("Show window", null, (_, _) => ShowRequested?.Invoke()) { Font = new Font(WinForms.Control.DefaultFont, FontStyle.Bold) });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_micItem);
        menu.Items.Add(_appItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke()));

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "VoxShift",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) ShowRequested?.Invoke();
        };
    }

    public event Action? ShowRequested;

    public event Action? MicrophoneRequested;

    public event Action? ApplicationRequested;

    public event Action? ExitRequested;

    public void Update(bool microphoneMode, string tooltip)
    {
        _micItem.Checked = microphoneMode;
        _appItem.Checked = !microphoneMode;
        // NotifyIcon.Text is limited to 127 characters.
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(2000);
    }

    private static Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } path)
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch (Exception)
        {
            // fall through
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
