using Microsoft.Win32;
using System.IO;

namespace AIUsageMonitor.Services;

public sealed class WindowsStartupService
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "AIUsageMonitor";

    public bool IsEnabled()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return runKey?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public bool TrySetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    return false;
                }

                using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                runKey.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
                return true;
            }

            using var existingRunKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            existingRunKey?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
