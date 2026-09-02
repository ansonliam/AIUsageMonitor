using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AIUsageMonitor.Services;

/// <summary>
/// Hands the memory freed by closing a window back to the OS instead of waiting for the GC to get
/// around to it. Closing a Window only makes it unreachable; the heap it occupied stays committed
/// until a collection runs, and the pages stay in the working set even after that, so nothing the
/// user can see in Task Manager changes on its own for some time.
/// </summary>
internal static class MemoryReclaimer
{
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromHours(1);

    // Growth in committed heap since the last pass below which the app counts as having been idle.
    // A tray app that has only ticked over a few refreshes has nothing worth a blocking compacting
    // collection, and trimming its working set would only page out a widget the user may be
    // looking at - so when nothing has accumulated, leave it alone.
    //
    // Committed bytes, deliberately, not GC.GetTotalMemory: that counts everything allocated since
    // the last collection, so on a process where no collection ever runs it climbs with ordinary
    // churn - measured here at ~10MB a minute while the committed heap did not move at all. It
    // would trip this guard every time and reclaim nothing. Committed bytes are what the process
    // is actually holding, which is what a pass can hand back.

    private const long IdleGrowthBytes = 16L * 1024 * 1024;

    private static int _startupReclaimed;
    private static DispatcherTimer? _periodicTimer;
    private static long _committedAfterLastReclaim;

    /// <summary>
    /// Reclaims the startup peak once the first refresh has landed. Building the dashboard and
    /// scanning the session logs both allocate hard, and at startup they overlap, so the heap is
    /// grown to cover both bursts at once. The app then goes idle - it barely allocates, so no
    /// collection is ever triggered, the dead segments are never compacted, and Windows has no
    /// reason to trim the pages. Closing the dashboard already hands that peak back; this does
    /// the same without making the user close anything.
    /// </summary>
    public static void ReclaimAfterStartup(Dispatcher dispatcher)
    {
        // RefreshCompleted fires from a background thread, once per provider, and several can
        // land at the same moment. Only the first gets to schedule the pass.
        if (Interlocked.Exchange(ref _startupReclaimed, 1) != 0)
        {
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Reclaim);
    }

    /// <summary>
    /// Starts the recurring pass that catches what the startup and window-close reclaims cannot:
    /// the heap a long-running session grows into on its own, from refreshes, log scans and update
    /// checks, none of which is ever big enough on its own to trigger a collection in a process
    /// that spends its life idle.
    /// </summary>
    public static void StartPeriodicReclaim(Dispatcher dispatcher)
    {
        if (_periodicTimer is not null)
        {
            return;
        }

        _committedAfterLastReclaim = CommittedBytes();

        // Idle priority so the tick waits for a dispatcher that has nothing better to do, rather
        // than freezing a drag or a resize mid-gesture for the length of a gen-2 collection.
        _periodicTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
        {
            Interval = PeriodicInterval,
        };
        _periodicTimer.Tick += (_, _) => ReclaimIfGrown();
        _periodicTimer.Start();
    }

    private static void ReclaimIfGrown()
    {
        if (CommittedBytes() - _committedAfterLastReclaim < IdleGrowthBytes)
        {
            return;
        }

        Reclaim();
    }

    private static long CommittedBytes() => GC.GetGCMemoryInfo().TotalCommittedBytes;

    /// <summary>
    /// Reclaims once the dispatcher queue has drained. WPF tears a window down across several
    /// dispatcher passes (visual tree release, HWND destruction, its own deferred cleanup), so
    /// collecting straight from Closed would run while the window is still rooted.
    /// </summary>
    public static void ReclaimAfterWindowClose(Dispatcher dispatcher) =>
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Reclaim);

    private static void Reclaim()
    {
        // Two passes: the first queues the window's finalizable resources, the second collects the
        // objects those finalizers released.
        Collect();
        GC.WaitForPendingFinalizers();
        Collect();

        // The heap is now free but the pages are still charged to this process. Trimming pushes
        // them out of the working set, which is the number Task Manager reports.
        _ = SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));

        // Every entry point lands here on the dispatcher thread, so this needs no synchronisation.
        // Restarting the timer measures the next hour from the reclaim that actually happened:
        // closing the dashboard has just done this work, and repeating it an unrelated few minutes
        // later would find nothing to collect.
        _committedAfterLastReclaim = CommittedBytes();
        if (_periodicTimer is { IsEnabled: true } timer)
        {
            timer.Stop();
            timer.Start();
        }
    }

    private static void Collect()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr process,
        IntPtr minimumWorkingSetSize,
        IntPtr maximumWorkingSetSize);
}
