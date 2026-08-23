using System.Runtime.InteropServices;

namespace AIUsageMonitor.Services;

public interface ISystemIdleTimeProvider
{
    TimeSpan GetIdleTime();
}

public sealed class SystemIdleTimeProvider : ISystemIdleTimeProvider
{
    public TimeSpan GetIdleTime()
    {
        var lastInputInfo = new LastInputInfo
        {
            cbSize = (uint)Marshal.SizeOf<LastInputInfo>()
        };

        if (!GetLastInputInfo(ref lastInputInfo))
        {
            return TimeSpan.Zero;
        }

        // Both values use the same 32-bit millisecond clock. Unsigned subtraction also handles
        // the clock wrapping approximately every 49.7 days.
        var elapsedMilliseconds = unchecked((uint)Environment.TickCount - lastInputInfo.dwTime);
        return TimeSpan.FromMilliseconds(elapsedMilliseconds);
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }
}
