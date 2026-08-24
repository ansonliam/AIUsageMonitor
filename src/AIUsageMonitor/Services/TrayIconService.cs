using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AIUsageMonitor.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly IApplicationController _applicationController;
    private readonly GitHubReleaseService _gitHubReleaseService;
    private Drawing.Icon? _applicationIcon;
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _updateMenuItem;
    private Uri? _updateReleaseUrl;

    public TrayIconService(
        IApplicationController applicationController,
        GitHubReleaseService gitHubReleaseService)
    {
        _applicationController = applicationController;
        _gitHubReleaseService = gitHubReleaseService;
    }

    public void Initialize()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        _updateMenuItem = new Forms.ToolStripMenuItem { Visible = false };
        var windowWidgetMenuItem = new Forms.ToolStripMenuItem();
        var taskbarWidgetMenuItem = new Forms.ToolStripMenuItem();
        _updateMenuItem.Click += (_, _) => OpenUpdateRelease();
        windowWidgetMenuItem.Click += (_, _) => ToggleWindowWidget();
        taskbarWidgetMenuItem.Click += (_, _) => ToggleTaskbarWidget();
        menu.Opening += (_, _) => UpdateWidgetVisibilityMenuItems(windowWidgetMenuItem, taskbarWidgetMenuItem);

        menu.Items.Add(_updateMenuItem);
        menu.Items.Add(windowWidgetMenuItem);
        menu.Items.Add(taskbarWidgetMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
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
        _ = ShowUpdateIndicatorAsync();
    }

    private async Task ShowUpdateIndicatorAsync()
    {
        var result = await _gitHubReleaseService.CheckAsync();
        if (!result.IsUpdateAvailable || _notifyIcon is null || _applicationIcon is null)
        {
            return;
        }

        var updateIcon = CreateUpdateAvailableIcon(_applicationIcon);
        _notifyIcon.Icon = updateIcon;
        _notifyIcon.Text = $"AI Usage Monitor - Update available: {result.LatestReleaseTag}";
        _updateReleaseUrl = result.ReleaseUrl;
        if (_updateMenuItem is not null)
        {
            _updateMenuItem.Text = $"Update available: {result.LatestReleaseTag}";
            _updateMenuItem.Visible = true;
        }
        _applicationIcon.Dispose();
        _applicationIcon = updateIcon;
    }

    private void OpenUpdateRelease()
    {
        if (_updateReleaseUrl is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(_updateReleaseUrl.AbsoluteUri)
        {
            UseShellExecute = true
        });
    }

    private void ToggleWindowWidget()
    {
        if (_applicationController.IsMainWindowVisible())
        {
            _applicationController.HideMainWindow();
            return;
        }

        _applicationController.ShowMainWindow();
    }

    private void ToggleTaskbarWidget()
    {
        _applicationController.SetTaskbarWidgetVisibility(!_applicationController.IsTaskbarWidgetVisible());
    }

    private void UpdateWidgetVisibilityMenuItems(
        Forms.ToolStripMenuItem windowWidgetMenuItem,
        Forms.ToolStripMenuItem taskbarWidgetMenuItem)
    {
        windowWidgetMenuItem.Text = _applicationController.IsMainWindowVisible()
            ? "Hide Window Widget"
            : "Open Window Widget";
        taskbarWidgetMenuItem.Text = _applicationController.IsTaskbarWidgetVisible()
            ? "Hide Taskbar Widget"
            : "Open Taskbar Widget";
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
        _updateMenuItem = null;
        _updateReleaseUrl = null;
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

    private static Drawing.Icon CreateUpdateAvailableIcon(Drawing.Icon source)
    {
        using var bitmap = source.ToBitmap();
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            var diameter = Math.Max(5, bitmap.Width / 3);
            var offset = Math.Max(1, bitmap.Width - diameter - 1);
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x28, 0xC7, 0x6F));
            using var outline = new Drawing.Pen(Drawing.Color.White, 1.5f);
            graphics.FillEllipse(fill, offset, offset, diameter, diameter);
            graphics.DrawEllipse(outline, offset, offset, diameter, diameter);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporaryIcon = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temporaryIcon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
