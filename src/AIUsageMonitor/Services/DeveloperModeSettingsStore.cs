using System.IO;
using System.Text.Json;

namespace AIUsageMonitor.Services;

public sealed class DeveloperModeSettingsStore
{
    private readonly string _settingsPath;

    public DeveloperModeSettingsStore(string? dataDirectory = null)
    {
        var directory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIUsageMonitor");
        _settingsPath = Path.Combine(directory, "developer-settings.json");
    }

    public bool LoadEnabled()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return false;
            }

            var settings = JsonSerializer.Deserialize<DeveloperModeSettings>(File.ReadAllText(_settingsPath));
            return settings?.Enabled == true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public bool TrySaveEnabled(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var temporaryPath = _settingsPath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new DeveloperModeSettings { Enabled = enabled }));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class DeveloperModeSettings
    {
        public bool Enabled { get; init; }
    }
}
