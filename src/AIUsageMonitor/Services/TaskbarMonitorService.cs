using Forms = System.Windows.Forms;

namespace AIUsageMonitor.Services;

public sealed record TaskbarMonitor(
    string Id,
    string DisplayName,
    IntPtr TaskbarHandle,
    bool IsPrimary,
    bool HasTrayIcons);

public sealed class TaskbarMonitorService
{
    public IReadOnlyList<TaskbarMonitor> GetMonitors()
    {
        var taskbarHandles = new List<IntPtr>();
        var primaryTaskbar = TaskbarInterop.FindTaskbar();
        if (primaryTaskbar != IntPtr.Zero)
        {
            taskbarHandles.Add(primaryTaskbar);
        }

        taskbarHandles.AddRange(TaskbarInterop.FindSecondaryTaskbars());
        var screens = Forms.Screen.AllScreens;
        return taskbarHandles
            .Select(handle => (
                Handle: handle,
                Screen: Forms.Screen.FromHandle(handle),
                HasTrayIcons: TaskbarInterop.FindTrayNotifyArea(handle) != IntPtr.Zero))
            .GroupBy(entry => entry.Screen.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var entry = group.First();
                var displayNumber = Array.FindIndex(screens, screen =>
                    string.Equals(screen.DeviceName, entry.Screen.DeviceName, StringComparison.OrdinalIgnoreCase)) + 1;
                var displayName = displayNumber > 0 ? $"Display {displayNumber}" : entry.Screen.DeviceName;
                if (entry.Screen.Primary)
                {
                    displayName += " (Primary)";
                }
                if (entry.HasTrayIcons)
                {
                    displayName += " (Tray icons)";
                }

                return new TaskbarMonitor(entry.Screen.DeviceName, displayName, entry.Handle, entry.Screen.Primary, entry.HasTrayIcons);
            })
            .OrderByDescending(monitor => monitor.HasTrayIcons)
            .ThenByDescending(monitor => monitor.IsPrimary)
            .ThenBy(monitor => monitor.DisplayName)
            .ToArray();
    }
}
