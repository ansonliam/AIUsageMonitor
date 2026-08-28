using System.Runtime.InteropServices;

namespace AIUsageMonitor.Services;

// P/Invoke surface for locating the real Windows taskbar/tray and making our own widget window a
// topmost, non-activating tool window that floats beside it. Deliberately does not use SetParent
// into Shell_TrayWnd - that reparenting trick is unsupported by Explorer and known to misbehave
// across processes with different DPI-awareness, so this only ever floats a popup positioned to
// match the taskbar's real rect.
//
// Also answers whether a monitor is currently covered by a fullscreen app, which is what lets the
// widget stand down instead of drawing over a game or a fullscreen video - see
// IsMonitorCoveredByFullscreenWindow.
internal static class TaskbarInterop
{
    public const int GwlExStyle = -20;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExAppWindow = 0x00040000;
    public const int WsExNoActivate = 0x08000000;
    public const int WsExTopMost = 0x00000008;
    public const int WmDisplayChange = 0x007E;
    public const int WmWindowPosChanging = 0x0046;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoOwnerZOrder = 0x0200;
    public const uint SwpFrameChanged = 0x0020;
    public static readonly IntPtr HwndTopmost = new(-1);

    public const uint MonitorDefaultToNull = 0x00000000;
    public const uint GaRoot = 2;
    public const int DwmwaCloaked = 14;

    // How far a window's edge may sit from the monitor's edge and still count as fullscreen.
    // Deliberately tiny: a *maximized* window's rect is inflated past the monitor edges by the
    // invisible resize border (~8px at default settings), and the whole point of this comparison
    // is to tell those two apart - a generous tolerance would classify every maximized window as
    // fullscreen and hide the widget whenever any window was maximized.
    private const int FullscreenEdgeTolerance = 2;

    // Mirrors the Win32 WINDOWPOS struct pointed to by WM_WINDOWPOSCHANGING/CHANGED's lParam.
    // Sent to a window when ITS OWN position or z-order is about to change, synchronously and
    // before the change is applied, so rewriting hwndInsertAfter here overrides that change.
    // Importantly this is NOT sent when a sibling merely jumps above us (e.g. Explorer raising
    // Shell_TrayWnd to the top of the topmost band): our window hasn't moved, so there is no
    // message - Windows never notifies a window that something else went in front of it.
    [StructLayout(LayoutKind.Sequential)]
    public struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr HwndInsertAfter;
        public int X;
        public int Y;
        public int Cx;
        public int Cy;
        public uint Flags;
    }

    // WinEvent hook constants (SetWinEventHook) - system-wide, out-of-process notifications, not
    // to be confused with window messages. EventSystemForeground fires the instant any window
    // anywhere becomes the foreground window; EventObjectLocationChange fires whenever any
    // window's position/size changes (very high volume system-wide, hence always filtering by
    // hwnd in the callback). WinEventOutOfContext means the callback is delivered on the calling
    // thread's own message queue - no DLL injection into other processes, unlike WINEVENT_INCONTEXT.
    public const uint EventSystemForeground = 0x0003;
    public const uint EventObjectLocationChange = 0x800B;
    public const uint WinEventOutOfContext = 0x0000;
    public const uint WinEventSkipOwnProcess = 0x0002;
    public const int ObjIdWindow = 0;

    public delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PointL
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(PointL point);

    // Answers "is something drawn in front of us at this pixel" in a single call: WindowFromPoint
    // returns whichever window is topmost at that point. Used to skip redundant SetWindowPos
    // calls - this window is layered (AllowsTransparency), so every needless z-order change costs
    // a repaint, which is itself visible as a flicker.
    //
    // Windows we own are excluded, but only when they are themselves topmost. That split is the
    // point of the check: our own topmost popups are menus, tooltips and dropdowns (the tray
    // icon's context menu among them, which Windows is free to lay over the taskbar strip this
    // widget sits in) - raising ourselves above one of those hides part of a menu the user is
    // reading. A window of ours that is NOT topmost cannot be in front of us at all unless we
    // have dropped out of the topmost band, which is exactly the state worth recovering from.
    public static bool IsObscuredAt(IntPtr hWnd, int x, int y)
    {
        var topMostAtPoint = WindowFromPoint(new PointL { X = x, Y = y });
        if (topMostAtPoint == IntPtr.Zero || topMostAtPoint == hWnd)
        {
            return false;
        }

        // WindowFromPoint returns the deepest child under the point; the process and the ex-style
        // that matter here both live on the top-level window.
        var root = GetAncestor(topMostAtPoint, GaRoot);
        if (root == IntPtr.Zero)
        {
            root = topMostAtPoint;
        }

        return root != hWnd && !IsOwnTopMostWindow(root);
    }

    private static bool IsOwnTopMostWindow(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out var processId);
        return processId == (uint)Environment.ProcessId &&
               (GetWindowExStyle(hWnd).ToInt64() & WsExTopMost) != 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    public delegate bool EnumWindowsDelegate(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsDelegate callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string lpString);

    // GetWindowLongPtr/SetWindowLongPtr only exist as such on 64-bit user32; on a 32-bit process
    // the plain 32-bit entry points must be used instead, or the export is missing at load time.
    public static IntPtr GetWindowExStyle(IntPtr hWnd) =>
        Environment.Is64BitProcess
            ? GetWindowLongPtr64(hWnd, GwlExStyle)
            : new IntPtr(GetWindowLong32(hWnd, GwlExStyle));

    public static void SetWindowExStyle(IntPtr hWnd, IntPtr style)
    {
        if (Environment.Is64BitProcess)
        {
            SetWindowLongPtr64(hWnd, GwlExStyle, style);
        }
        else
        {
            SetWindowLong32(hWnd, GwlExStyle, style.ToInt32());
        }
    }

    /// <summary>Makes an independent popup ineligible for Alt+Tab without parenting it to
    /// Explorer. The frame refresh is explicitly non-activating.</summary>
    public static void ApplyNonActivatingToolWindowStyle(IntPtr hWnd)
    {
        var current = GetWindowExStyle(hWnd).ToInt64();
        var updated = (current | WsExToolWindow | WsExNoActivate) & ~WsExAppWindow;
        SetWindowExStyle(hWnd, new IntPtr(updated));
        SetWindowPos(
            hWnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpFrameChanged);
    }

    // Explorer periodically re-asserts its own topmost position (e.g. when a taskbar button is
    // clicked), which can bump other topmost windows down a slot in the topmost band even though
    // they never lost the Topmost flag itself. WPF's Window.Topmost only asks Win32 for
    // HWND_TOPMOST once (plus best-effort reactive handling); re-issuing this explicitly on every
    // reposition tick is what keeps the widget from ending up rendered behind the taskbar.
    public static void ForceTopMost(IntPtr hWnd) =>
        SetWindowPos(hWnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public Rect RcMonitor;
        public Rect RcWork;
        public uint DwFlags;
    }

    // Public because the very high volume EVENT_OBJECT_LOCATIONCHANGE callback uses it to filter
    // down to "did the window the user is actually in just change size" before doing any work.
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    // Windows that legitimately span an entire monitor without being an app in fullscreen: the
    // desktop itself (Progman hosts the icons, WorkerW hosts the wallpaper, #32769 is the root
    // desktop window), the shell's own taskbars, and the shell's full-screen overlays.
    // Clicking the desktop makes Progman/WorkerW the foreground window, so without this the
    // widget would vanish on every desktop click.
    //
    // XamlExplorerHostIslandWindow is alt-tab, Task View (Win+Tab) and Snap Assist. Those really
    // do cover the monitor exactly, but they also cover the taskbar, so hiding for them buys
    // nothing the user can see - and it would cost a Hide/Show repaint on a layered window every
    // single alt-tab. No real application uses the class.
    private static readonly string[] ShellClassNames =
    [
        "Progman",
        "WorkerW",
        "#32769",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "XamlExplorerHostIslandWindow"
    ];

    // Split out from IsFullscreenOn purely so the list itself is reachable from a test - the rest
    // of that method needs a live window handle and cannot be exercised offline.
    public static bool IsShellSurfaceClass(string className) => ShellClassNames.Contains(className);

    // Answers "should the widget get out of the way right now" for the monitor that the given
    // window (in practice, that monitor's taskbar) sits on.
    //
    // This exists because "sit above the taskbar but below a fullscreen app" is not a z-order
    // position that can be expressed: the taskbar is topmost, a fullscreen app usually is not, so
    // anything drawn above the taskbar is necessarily above the fullscreen app too. Windows
    // solves this for its own taskbar by detecting fullscreen and standing down, and so do we.
    //
    // Two independent probes, either of which is enough:
    //   * the foreground window - catches a fullscreen app whose centre pixel happens to be
    //     covered by something else (a game/chat overlay), which the hit-test below would miss;
    //   * whatever is actually drawn at the centre of the monitor - catches a fullscreen window
    //     that is NOT foreground, e.g. a fullscreen video left playing on a second display while
    //     the user works on the primary one, which the foreground probe would miss.
    //
    // Known limitation: if the taskbar on this monitor is set to auto-hide, the work area equals
    // the full monitor, so a merely maximized window matches the fullscreen test and hides the
    // widget. Accepted deliberately - the alternative (also demanding the window lack a caption)
    // rejects real fullscreen windows, and a false negative there is the bug being fixed.
    public static bool IsMonitorCoveredByFullscreenWindow(IntPtr monitorAnchor)
    {
        if (monitorAnchor == IntPtr.Zero)
        {
            return false;
        }

        var monitor = MonitorFromWindow(monitorAnchor, MonitorDefaultToNull);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        if (IsFullscreenOn(GetForegroundWindow(), info.RcMonitor))
        {
            return true;
        }

        var center = new PointL
        {
            X = (info.RcMonitor.Left + info.RcMonitor.Right) / 2,
            Y = (info.RcMonitor.Top + info.RcMonitor.Bottom) / 2
        };

        // GetAncestor(GA_ROOT) because WindowFromPoint returns the deepest child under the point
        // (a render surface, a video control), whose rect is not the one worth comparing.
        return IsFullscreenOn(GetAncestor(WindowFromPoint(center), GaRoot), info.RcMonitor);
    }

    private static bool IsFullscreenOn(IntPtr hWnd, Rect monitorRect) =>
        hWnd != IntPtr.Zero &&
        IsWindowVisible(hWnd) &&
        !IsCloaked(hWnd) &&
        GetWindowRect(hWnd, out var windowRect) &&
        CoversMonitorExactly(windowRect, monitorRect) &&
        !IsShellSurfaceClass(ClassNameOf(hWnd));

    private static string ClassNameOf(IntPtr hWnd)
    {
        var className = new System.Text.StringBuilder(64);
        return GetClassName(hWnd, className, className.Capacity) > 0 ? className.ToString() : string.Empty;
    }

    // A UWP window can report itself visible while DWM is not rendering it at all (the classic
    // "ghost" ApplicationFrameWindow left behind by a suspended app). Those are frequently sized
    // to the whole monitor, so they would otherwise read as a permanent fullscreen app.
    private static bool IsCloaked(IntPtr hWnd) =>
        DwmGetWindowAttribute(hWnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    // Equality per edge rather than containment: see FullscreenEdgeTolerance for why a window
    // that merely *encloses* the monitor is the maximized case and must not count.
    public static bool CoversMonitorExactly(Rect windowRect, Rect monitorRect) =>
        Math.Abs(windowRect.Left - monitorRect.Left) <= FullscreenEdgeTolerance &&
        Math.Abs(windowRect.Top - monitorRect.Top) <= FullscreenEdgeTolerance &&
        Math.Abs(windowRect.Right - monitorRect.Right) <= FullscreenEdgeTolerance &&
        Math.Abs(windowRect.Bottom - monitorRect.Bottom) <= FullscreenEdgeTolerance;

    public static IntPtr FindTaskbar() => FindWindow("Shell_TrayWnd", null);

    public static IReadOnlyList<IntPtr> FindSecondaryTaskbars()
    {
        var handles = new List<IntPtr>();
        EnumWindows((handle, _) =>
        {
            var className = new System.Text.StringBuilder(64);
            if (GetClassName(handle, className, className.Capacity) > 0 &&
                className.ToString() == "Shell_SecondaryTrayWnd")
            {
                handles.Add(handle);
            }

            return true;
        }, IntPtr.Zero);
        return handles;
    }

    public static IntPtr FindTrayNotifyArea(IntPtr taskbarHandle) =>
        taskbarHandle == IntPtr.Zero
            ? IntPtr.Zero
            : FindWindowEx(taskbarHandle, IntPtr.Zero, "TrayNotifyWnd", null);
}
