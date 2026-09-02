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
