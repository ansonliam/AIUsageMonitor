namespace AIUsageMonitor.Services;

// Tracks whether a menu this app owns is currently on screen.
//
// Exists for the taskbar widget's z-order enforcement, which otherwise fights our own menus and
// wins: menus are laid out against the screen bounds rather than the work area, so both the tray
// icon's menu and the widget's own menu routinely extend down over the taskbar strip the widget
// sits in. The widget re-claims the top of the topmost band whenever it finds itself covered, and
// doing that while a menu is open draws the widget over whichever item happens to overlap it -
// for the tray menu that is the bottom item, "Close".
//
// A counter rather than a bool because more than one menu can be open at a time (a menu can be
// opened from the settings window while the tray menu is still up), and the first one closing
// must not clear the state for the other.
internal static class AppMenuState
{
    private static int _openMenuCount;

    public static bool IsMenuOpen => Volatile.Read(ref _openMenuCount) > 0;

    public static void MenuOpened() => Interlocked.Increment(ref _openMenuCount);

    // Floors at zero so an unpaired close - a menu whose Opened never fired, or a close raised
    // twice - can't drive the count negative and wedge the widget into permanently standing down.
    public static void MenuClosed()
    {
        var count = Volatile.Read(ref _openMenuCount);
        while (count > 0)
        {
            var previous = Interlocked.CompareExchange(ref _openMenuCount, count - 1, count);
            if (previous == count)
            {
                return;
            }

            count = previous;
        }
    }
}
