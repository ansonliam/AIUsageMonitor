using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

// Covers the one piece of the fullscreen check that is pure geometry rather than a live window
// tree: telling a genuinely fullscreen window apart from a merely maximized one, which is the
// distinction the taskbar widget's hide-for-fullscreen behaviour rests on.
[TestClass]
public sealed class TaskbarFullscreenTests
{
    private static readonly TaskbarInterop.Rect Monitor = new() { Left = 0, Top = 0, Right = 2560, Bottom = 1440 };

    private static TaskbarInterop.Rect Rect(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };

    [TestMethod]
    public void FullscreenWindow_MatchingTheMonitorExactly_Covers()
    {
        Assert.IsTrue(TaskbarInterop.CoversMonitorExactly(Rect(0, 0, 2560, 1440), Monitor));
    }

    [TestMethod]
    public void MaximizedWindow_InflatedByItsInvisibleResizeBorder_DoesNotCover()
    {
        // What GetWindowRect actually reports for a maximized window: the work area pushed out by
        // the frame on every side. A containment test (left <= / right >= ...) would call this
        // fullscreen and hide the widget behind every maximized window.
        Assert.IsFalse(TaskbarInterop.CoversMonitorExactly(Rect(-8, -8, 2568, 1448), Monitor));
    }

    [TestMethod]
    public void MaximizedWindow_ClippedToTheWorkArea_DoesNotCover()
    {
        // The zero-border variant: still shorter than the monitor, because the taskbar is there -
        // and a monitor without a taskbar never hosts this widget in the first place.
        Assert.IsFalse(TaskbarInterop.CoversMonitorExactly(Rect(0, 0, 2560, 1392), Monitor));
    }

    [TestMethod]
    public void FullscreenWindow_OffByOneEdge_StillCovers()
    {
        Assert.IsTrue(TaskbarInterop.CoversMonitorExactly(Rect(-1, 0, 2561, 1440), Monitor));
    }

    [TestMethod]
    public void WindowOnAnotherMonitor_DoesNotCover()
    {
        // How a fullscreen app on a different display is excluded: the comparison is against this
        // widget's own monitor rect, so no separate monitor lookup is needed for the candidate.
        Assert.IsFalse(TaskbarInterop.CoversMonitorExactly(Rect(2560, 0, 5120, 1440), Monitor));
    }

    [TestMethod]
    public void ShellSurfaces_SpanningTheMonitor_AreNotTreatedAsFullscreen()
    {
        // The desktop is the foreground window on every desktop click, and the alt-tab / Task View
        // overlay matches the monitor rect exactly - both would otherwise hide the widget, the
        // second one on every single alt-tab.
        Assert.IsTrue(TaskbarInterop.IsShellSurfaceClass("Progman"));
        Assert.IsTrue(TaskbarInterop.IsShellSurfaceClass("WorkerW"));
        Assert.IsTrue(TaskbarInterop.IsShellSurfaceClass("XamlExplorerHostIslandWindow"));
    }

    [TestMethod]
    public void ApplicationWindowClasses_AreNotTreatedAsShellSurfaces()
    {
        // Chrome's fullscreen window is the case this whole check exists to catch.
        Assert.IsFalse(TaskbarInterop.IsShellSurfaceClass("Chrome_WidgetWin_1"));
        Assert.IsFalse(TaskbarInterop.IsShellSurfaceClass("ApplicationFrameWindow"));
    }

    [TestMethod]
    public void SmallWindow_DoesNotCover()
    {
        Assert.IsFalse(TaskbarInterop.CoversMonitorExactly(Rect(400, 300, 1200, 900), Monitor));
    }
}
