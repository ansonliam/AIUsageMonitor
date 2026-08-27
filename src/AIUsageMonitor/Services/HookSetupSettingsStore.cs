using System.Text.Json;
using System.IO;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class HookSetupSettingsStore
{
    private readonly string _settingsPath;

    public HookSetupSettingsStore(string? dataDirectory = null)
    {
        var directory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIUsageMonitor");
        _settingsPath = Path.Combine(directory, "hook-setup.json");
    }

    public HookSetupSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new HookSetupSettings();
            }

            return JsonSerializer.Deserialize<HookSetupSettings>(File.ReadAllText(_settingsPath))
                ?? new HookSetupSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new HookSetupSettings();
        }
    }

    public bool TrySave(HookSetupSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var temporaryPath = _settingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
