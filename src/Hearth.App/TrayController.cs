using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Hearth.App;

/// <summary>Keeps the launcher reachable while its main window is hidden.</summary>
internal sealed class TrayController : IDisposable
{
    private readonly Window window;
    private readonly Drawing.Icon icon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Forms.NotifyIcon notification;
    private WindowState restoreState = WindowState.Normal;
    private bool explainedClose;

    public TrayController(Window window, Action quit)
    {
        this.window = window;
        if (window.WindowState != WindowState.Minimized) restoreState = window.WindowState;
        using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Hearth.App;component/Assets/Hearth.ico"))?.Stream
            ?? throw new IOException("Hearth's tray icon could not be loaded.");
        using var sourceIcon = new Drawing.Icon(resource);
        icon = (Drawing.Icon)sourceIcon.Clone();
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Hearth", null, (_, _) => Restore());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Hearth", null, (_, _) => { Restore(); quit(); });
        notification = new Forms.NotifyIcon
        {
            Text = "Hearth — Modpack Launcher",
            Icon = icon,
            ContextMenuStrip = menu
        };
        notification.DoubleClick += (_, _) => Restore();
        notification.BalloonTipClicked += (_, _) => Restore();
        try { notification.Visible = true; }
        catch { notification.Dispose(); menu.Dispose(); icon.Dispose(); throw; }
        window.StateChanged += RememberWindowState;
    }

    public void HideWindow()
    {
        window.Hide();
        if (explainedClose) return;
        explainedClose = true;
        notification.ShowBalloonTip(3500, "Hearth is still running", "Use the tray icon to reopen Hearth or choose Quit Hearth.", Forms.ToolTipIcon.Info);
    }

    public void Restore()
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = restoreState;
        window.Show();
        window.Activate();
    }

    private void RememberWindowState(object? sender, EventArgs e)
    {
        if (window.WindowState != WindowState.Minimized) restoreState = window.WindowState;
    }

    public void Dispose()
    {
        window.StateChanged -= RememberWindowState;
        notification.Visible = false;
        notification.Dispose();
        menu.Dispose();
        icon.Dispose();
    }
}
