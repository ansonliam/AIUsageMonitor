using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AIUsageMonitor.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly IApplicationController _applicationController;
    private Drawing.Icon? _applicationIcon;
    private Forms.NotifyIcon? _notifyIcon;

    public TrayIconService(IApplicationController applicationController)
    {
        _applicationController = applicationController;
    }

    public void Initialize()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => _applicationController.ShowMainWindow());
        menu.Items.Add("Settings", null, (_, _) => _applicationController.ShowSettings());
        menu.Items.Add("Refresh All", null, async (_, _) => await _applicationController.RefreshAllAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Close", null, async (_, _) => await _applicationController.ExitAsync());

        _applicationIcon = LoadApplicationIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "AI Usage Monitor",
            Icon = _applicationIcon ?? Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => _applicationController.ShowMainWindow();
    }

    public void Dispose()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _notifyIcon = null;
        _applicationIcon?.Dispose();
        _applicationIcon = null;
    }

    private static Drawing.Icon? LoadApplicationIcon()
    {
        try
        {
            return Environment.ProcessPath is { } executablePath
                ? Drawing.Icon.ExtractAssociatedIcon(executablePath)
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
