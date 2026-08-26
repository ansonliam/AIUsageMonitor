using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AIUsageMonitor.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly IApplicationController _applicationController;
    private readonly UpdateAvailabilityMonitor _updateAvailabilityMonitor;
    private Drawing.Icon? _baseApplicationIcon;
    private Drawing.Icon? _updateAvailableIcon;
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _updateMenuItem;
    private Uri? _updateReleaseUrl;

    public TrayIconService(
        IApplicationController applicationController,
        UpdateAvailabilityMonitor updateAvailabilityMonitor)
    {
        _applicationController = applicationController;
        _updateAvailabilityMonitor = updateAvailabilityMonitor;
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
        // The taskbar widget floats above the taskbar and re-claims the top of the topmost band
        // whenever it finds itself covered. This menu is laid out against the screen bounds, so
        // its lower items sit over that strip - without standing the widget down for as long as
        // the menu is up, the widget is drawn over them.
        menu.Opened += (_, _) => AppMenuState.MenuOpened();
        menu.Closed += (_, _) => AppMenuState.MenuClosed();

        menu.Items.Add(_updateMenuItem);
        menu.Items.Add(windowWidgetMenuItem);
        menu.Items.Add(taskbarWidgetMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Settings", null, (_, _) => _applicationController.ShowSettings());
        menu.Items.Add("Refresh All", null, async (_, _) => await _applicationController.RefreshAllAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Close", null, async (_, _) => await _applicationController.ExitAsync());

        _baseApplicationIcon = LoadApplicationIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "AI Usage Monitor",
            Icon = _baseApplicationIcon ?? Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => _applicationController.ShowMainWindow();
        // The monitor re-checks GitHub periodically for the lifetime of the tray session (not just
        // once at startup), so this needs to react every time it fires, not just the first.
        _updateAvailabilityMonitor.UpdateChecked += ApplyUpdateResult;
    }

    private void ApplyUpdateResult(GitHubReleaseCheckResult result)
    {
        if (_notifyIcon is null || _baseApplicationIcon is null)
        {
            return;
        }

        if (!result.IsUpdateAvailable)
        {
            _notifyIcon.Icon = _baseApplicationIcon;
            _notifyIcon.Text = "AI Usage Monitor";
            _updateReleaseUrl = null;
            if (_updateMenuItem is not null)
            {
                _updateMenuItem.Visible = false;
            }
            return;
        }

        var updateLabel = result.IsCritical
            ? $"Critical update available: {result.LatestReleaseTag}"
            : $"Update available: {result.LatestReleaseTag}";
        _updateAvailableIcon ??= CreateUpdateAvailableIcon(_baseApplicationIcon);
        _notifyIcon.Icon = _updateAvailableIcon;
        _notifyIcon.Text = $"AI Usage Monitor - {updateLabel}";
        _updateReleaseUrl = result.ReleaseUrl;
        if (_updateMenuItem is not null)
        {
            _updateMenuItem.Text = updateLabel;
            _updateMenuItem.Visible = true;
        }
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
        _updateAvailabilityMonitor.UpdateChecked -= ApplyUpdateResult;

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
        _baseApplicationIcon?.Dispose();
        _baseApplicationIcon = null;
        _updateAvailableIcon?.Dispose();
        _updateAvailableIcon = null;
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
