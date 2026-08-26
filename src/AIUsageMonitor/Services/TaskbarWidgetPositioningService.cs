using System.Windows;
using System.Windows.Media;

namespace AIUsageMonitor.Services;

// Computes where the taskbar widget should sit: flush against the left edge of the tray icon
// cluster (TrayNotifyWnd) when that taskbar owns the tray, or offset from the taskbar's right edge
// on a secondary display. Windows 11 draws secondary clocks inside its XAML surface without a
// measurable child window, so the per-monitor offset is user-adjustable.
// GetWindowRect returns physical pixels; WPF's Window.Left/Top are DIPs, so results
// are converted through the same CompositionTarget.TransformFromDevice approach MainWindow already
// uses for its own screen-relative placement (see MainWindow.SnapToBottomRight).
//
// The window's own height is set to match the taskbar's full height (not just its content) so
// its hit-test bounds cover the whole strip - otherwise a window sized to its (shorter) content
// and merely centered within the taskbar's height leaves a sliver of the real Shell_TrayWnd
// exposed above/below it, which then swallows clicks meant for this widget (e.g. right-click
// showing the taskbar's own context menu instead of this one). The content itself is centered
// vertically inside that taller window via VerticalAlignment, not by the window's own placement.
public sealed class TaskbarWidgetPositioningService
{
    // Guards against a transient/degenerate GetWindowRect reading (e.g. captured mid-transition
    // while a context menu or shell flyout is opening/closing near the taskbar) collapsing the
    // window to a near-zero height, which makes it invisible until something else happens to
    // trigger another reposition. A real taskbar is always much taller than this at any DPI.
    private const double MinimumPlausibleTaskbarHeightDip = 8;

    public bool TryComputePosition(
        Visual visual,
        double widgetWidthDip,
        IntPtr taskbarHandle,
        bool useTrayAnchor,
        bool alignLeft,
        double offsetDip,
        out double left,
        out double top,
        out double taskbarHeightDip)
    {
        left = 0;
        top = 0;
        taskbarHeightDip = 0;

        if (taskbarHandle == IntPtr.Zero || !TaskbarInterop.GetWindowRect(taskbarHandle, out var taskbarRect))
        {
            return false;
        }

        // Left alignment ignores the tray entirely - it is measured from the taskbar's own left
        // edge, not from wherever the tray icons happen to start.
        var trayNotifyHandle = useTrayAnchor && !alignLeft ? TaskbarInterop.FindTrayNotifyArea(taskbarHandle) : IntPtr.Zero;
        var anchorPixels = taskbarRect.Left;
        if (trayNotifyHandle != IntPtr.Zero && TaskbarInterop.GetWindowRect(trayNotifyHandle, out var trayRect))
        {
            anchorPixels = trayRect.Left;
        }

        var transform = PresentationSource.FromVisual(visual)?.CompositionTarget?.TransformFromDevice;
        if (transform is null)
        {
            return false;
        }

        var taskbarTopLeftDip = transform.Value.Transform(new System.Windows.Point(taskbarRect.Left, taskbarRect.Top));
        var taskbarBottomDip = transform.Value.Transform(new System.Windows.Point(taskbarRect.Left, taskbarRect.Bottom));
        var taskbarRightDip = transform.Value.Transform(new System.Windows.Point(taskbarRect.Right, taskbarRect.Top)).X;
        var anchorDip = transform.Value.Transform(new System.Windows.Point(anchorPixels, taskbarRect.Top)).X;

        taskbarHeightDip = taskbarBottomDip.Y - taskbarTopLeftDip.Y;
        if (taskbarHeightDip < MinimumPlausibleTaskbarHeightDip)
        {
            taskbarHeightDip = 0;
            return false;
        }

        left = alignLeft
            ? taskbarTopLeftDip.X + Math.Max(0, offsetDip)
            : trayNotifyHandle == IntPtr.Zero
                ? taskbarRightDip - widgetWidthDip - Math.Max(0, offsetDip)
                : anchorDip - widgetWidthDip;
        top = taskbarTopLeftDip.Y;
        return true;
    }
}
