using System.IO;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class CodexApiCostSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly object _syncRoot = new();
    private readonly string _settingsPath;

    public CodexApiCostSettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIUsageMonitor",
            "codex-api-cost-settings.json");
    }

    public CodexApiCostSettings Load()
    {
        lock (_syncRoot)
        {
            try
            {
                return File.Exists(_settingsPath)
                    ? JsonSerializer.Deserialize<CodexApiCostSettings>(File.ReadAllText(_settingsPath))
                        ?? new CodexApiCostSettings()
                    : new CodexApiCostSettings();
            }
            catch (JsonException)
            {
                return new CodexApiCostSettings();
            }
            catch (IOException)
            {
                return new CodexApiCostSettings();
            }
            catch (UnauthorizedAccessException)
            {
                return new CodexApiCostSettings();
            }
        }
    }

    public void Save(CodexApiCostSettings settings)
    {
        lock (_syncRoot)
        {
            try
            {
                // Multi-currency conversion is intentionally not implemented. All newly saved
                // endpoint rates are explicitly AUD; values themselves are never converted.
                foreach (var endpoint in settings.Endpoints)
                {
                    endpoint.Currency = "AUD";
                }

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
