using System.Windows;
using System.Windows.Media;

namespace AIUsageMonitor.Services;

// Computes where the taskbar widget should sit: flush against the left edge of the tray icon
// cluster (TrayNotifyWnd), vertically centered in the taskbar (Shell_TrayWnd), on the primary
// display only. GetWindowRect returns physical pixels; WPF's Window.Left/Top are DIPs, so results
// are converted through the same CompositionTarget.TransformFromDevice approach MainWindow already
// uses for its own screen-relative placement (see MainWindow.SnapToBottomRight).
public sealed class TaskbarWidgetPositioningService
{
    public bool TryComputePosition(Visual visual, double widgetWidthDip, double widgetHeightDip, out double left, out double top)
    {
        left = 0;
        top = 0;

        var taskbarHandle = TaskbarInterop.FindTaskbar();
        if (taskbarHandle == IntPtr.Zero || !TaskbarInterop.GetWindowRect(taskbarHandle, out var taskbarRect))
        {
            return false;
        }

        var trayNotifyHandle = TaskbarInterop.FindTrayNotifyArea(taskbarHandle);
        var trayLeftPixels = taskbarRect.Right;
        if (trayNotifyHandle != IntPtr.Zero && TaskbarInterop.GetWindowRect(trayNotifyHandle, out var trayRect))
        {
            trayLeftPixels = trayRect.Left;
        }

        var transform = PresentationSource.FromVisual(visual)?.CompositionTarget?.TransformFromDevice;
        if (transform is null)
        {
            return false;
        }

        var taskbarTopLeftDip = transform.Value.Transform(new System.Windows.Point(taskbarRect.Left, taskbarRect.Top));
        var taskbarBottomDip = transform.Value.Transform(new System.Windows.Point(taskbarRect.Left, taskbarRect.Bottom));
        var trayLeftDip = transform.Value.Transform(new System.Windows.Point(trayLeftPixels, taskbarRect.Top)).X;

        var taskbarHeightDip = taskbarBottomDip.Y - taskbarTopLeftDip.Y;
        left = trayLeftDip - widgetWidthDip;
        top = taskbarTopLeftDip.Y + (taskbarHeightDip - widgetHeightDip) / 2;
        return true;
    }
}
