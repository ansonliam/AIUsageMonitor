using System.Runtime.InteropServices;

namespace AIUsageMonitor.Services;

// P/Invoke surface for locating the real Windows taskbar/tray and making our own widget window a
// topmost, non-activating tool window that floats beside it. Deliberately does not use SetParent
// into Shell_TrayWnd - that reparenting trick is unsupported by Explorer and known to misbehave
// across processes with different DPI-awareness, so this only ever floats a popup positioned to
// match the taskbar's real rect.
internal static class TaskbarInterop
{
    public const int GwlExStyle = -20;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExNoActivate = 0x08000000;
    public const int WmDisplayChange = 0x007E;
    public const int WmWindowPosChanging = 0x0046;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoOwnerZOrder = 0x0200;
    public static readonly IntPtr HwndTopmost = new(-1);

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
    public static bool IsObscuredAt(IntPtr hWnd, int x, int y)
    {
        var topMostAtPoint = WindowFromPoint(new PointL { X = x, Y = y });
        return topMostAtPoint != IntPtr.Zero && topMostAtPoint != hWnd;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

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

    // Explorer periodically re-asserts its own topmost position (e.g. when a taskbar button is
    // clicked), which can bump other topmost windows down a slot in the topmost band even though
    // they never lost the Topmost flag itself. WPF's Window.Topmost only asks Win32 for
    // HWND_TOPMOST once (plus best-effort reactive handling); re-issuing this explicitly on every
    // reposition tick is what keeps the widget from ending up rendered behind the taskbar.
    public static void ForceTopMost(IntPtr hWnd) =>
        SetWindowPos(hWnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);

    public static IntPtr FindTaskbar() => FindWindow("Shell_TrayWnd", null);

    public static IntPtr FindTrayNotifyArea(IntPtr taskbarHandle) =>
        taskbarHandle == IntPtr.Zero
            ? IntPtr.Zero
            : FindWindowEx(taskbarHandle, IntPtr.Zero, "TrayNotifyWnd", null);
}
