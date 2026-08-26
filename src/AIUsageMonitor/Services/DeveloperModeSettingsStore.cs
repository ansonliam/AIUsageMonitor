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
        => LoadSettings().Enabled;

    public bool LoadSimulateUpdateAvailable()
        => LoadSettings().SimulateUpdateAvailable;

    public bool IsUpdateSimulationEnabled()
    {
        var settings = LoadSettings();
        return settings.Enabled && settings.SimulateUpdateAvailable;
    }

    public bool TrySaveEnabled(bool enabled)
        => TrySaveSettings(LoadSettings() with { Enabled = enabled });

    public bool TrySaveSimulateUpdateAvailable(bool simulateUpdateAvailable)
        => TrySaveSettings(LoadSettings() with { SimulateUpdateAvailable = simulateUpdateAvailable });

    private DeveloperModeSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new DeveloperModeSettings();
            }

            var settings = JsonSerializer.Deserialize<DeveloperModeSettings>(File.ReadAllText(_settingsPath));
            return settings ?? new DeveloperModeSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DeveloperModeSettings();
        }
    }

    private bool TrySaveSettings(DeveloperModeSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var temporaryPath = _settingsPath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(settings));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record DeveloperModeSettings
    {
        public bool Enabled { get; init; }
        public bool SimulateUpdateAvailable { get; init; }
    }
}
