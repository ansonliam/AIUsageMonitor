using System.IO;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

// Same shape as CodexApiCostSettingsStore/UsageCacheStore: JSON under %LOCALAPPDATA%\AIUsageMonitor,
// temp-file + atomic File.Move on save so a crash or power loss mid-write can never corrupt the
// saved dashboard layout.
public sealed class DashboardLayoutStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly object _syncRoot = new();
    private readonly string _layoutPath;

    public DashboardLayoutStore(string? layoutPath = null)
    {
        _layoutPath = layoutPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIUsageMonitor",
            "dashboard-layout.json");
    }

    public DashboardLayout Load()
    {
        lock (_syncRoot)
        {
            try
            {
                return File.Exists(_layoutPath)
                    ? JsonSerializer.Deserialize<DashboardLayout>(File.ReadAllText(_layoutPath))
                        ?? new DashboardLayout()
                    : new DashboardLayout();
            }
            catch (JsonException)
            {
                return new DashboardLayout();
            }
            catch (IOException)
            {
                return new DashboardLayout();
            }
            catch (UnauthorizedAccessException)
            {
                return new DashboardLayout();
            }
        }
    }

    public void Save(DashboardLayout layout)
    {
        lock (_syncRoot)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_layoutPath)!);
                var temporaryPath = _layoutPath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(layout, SerializerOptions));
                File.Move(temporaryPath, _layoutPath, overwrite: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // Reset Layout: deleting the file (rather than writing an empty one) makes the next Load()
    // return a fresh default DashboardLayout and lets DashboardLayoutViewModel re-run its normal
    // "place every known card from scratch" logic - no separate default-layout-as-JSON to keep in
    // sync with the code.
    public void Delete()
    {
        lock (_syncRoot)
        {
            try
            {
                if (File.Exists(_layoutPath))
                {
                    File.Delete(_layoutPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
