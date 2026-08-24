using System.IO;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

// Same shape as DashboardLayoutStore: JSON under %LOCALAPPDATA%\AIUsageMonitor, temp-file +
// atomic File.Move on save so a crash or power loss mid-write can never corrupt the saved settings.
public sealed class TaskbarWidgetSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly object _syncRoot = new();
    private readonly string _settingsPath;

    public TaskbarWidgetSettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIUsageMonitor",
            "taskbar-widget.json");
    }

    public TaskbarWidgetSettings Load()
    {
        lock (_syncRoot)
        {
            try
            {
                return File.Exists(_settingsPath)
                    ? JsonSerializer.Deserialize<TaskbarWidgetSettings>(File.ReadAllText(_settingsPath))
                        ?? new TaskbarWidgetSettings()
                    : new TaskbarWidgetSettings();
            }
            catch (JsonException)
            {
                return new TaskbarWidgetSettings();
            }
            catch (IOException)
            {
                return new TaskbarWidgetSettings();
            }
            catch (UnauthorizedAccessException)
            {
                return new TaskbarWidgetSettings();
            }
        }
    }

    public void Save(TaskbarWidgetSettings settings)
    {
        lock (_syncRoot)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
                var temporaryPath = _settingsPath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, SerializerOptions));
                File.Move(temporaryPath, _settingsPath, overwrite: true);
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
