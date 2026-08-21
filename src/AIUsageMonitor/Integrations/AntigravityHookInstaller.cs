using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIUsageMonitor.Integrations;

public sealed class AntigravityHookInstaller
{
    private const string NotifyArgument = "antigravity";
    private const string OwnedHookName = "com.ansonliam.ai-usage-monitor";

    public string ConfigurationPath => AntigravityInstallation.HooksPath;

    public HookInstallationStatus GetStatus()
    {
        if (!AntigravityInstallation.IsDetected())
        {
            return HookInstallationStatus.ClientNotDetected;
        }

        if (!File.Exists(ConfigurationPath))
        {
            return HookInstallationStatus.NotInstalled;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(ConfigurationPath)) as JsonObject;
            if (root is null)
            {
                return HookInstallationStatus.InvalidConfiguration;
            }

            var handlers = FindOwnedHandlers(root).ToList();
            if (handlers.Count == 0)
            {
                return HookInstallationStatus.NotInstalled;
            }

            var executable = Environment.ProcessPath;
            return handlers.Count == 1 &&
                   executable is not null &&
                   GetCommand(handlers[0])?.Contains(executable, StringComparison.OrdinalIgnoreCase) == true
                ? HookInstallationStatus.Installed
                : HookInstallationStatus.InvalidConfiguration;
        }
        catch (JsonException)
        {
            return HookInstallationStatus.InvalidConfiguration;
        }
        catch (IOException)
        {
            return HookInstallationStatus.InvalidConfiguration;
        }
        catch (InvalidOperationException)
        {
            return HookInstallationStatus.InvalidConfiguration;
        }
    }

    public async Task InstallOrRepairAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AntigravityInstallation.ConfigurationDirectory);
        JsonObject root;
        if (File.Exists(ConfigurationPath))
        {
            root = JsonNode.Parse(await File.ReadAllTextAsync(ConfigurationPath, cancellationToken)) as JsonObject
                ?? throw new InvalidOperationException("The existing Antigravity hooks file is invalid.");
        }
        else
        {
            root = new JsonObject();
        }

        if (root[OwnedHookName] is not null && root[OwnedHookName] is not JsonObject)
        {
            throw new InvalidOperationException("The existing AI Usage Monitor Antigravity hook is invalid.");
        }

        RemoveOwnedHandlers(root);
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The application path could not be determined.");
        var command = $"\"{executable}\" {string.Join(' ', HookProtocol.CreateArguments(NotifyArgument))}";
        root[OwnedHookName] = new JsonObject
        {
            ["Stop"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = 10
                }
            }
        };

        await WriteAsync(root, cancellationToken);
    }

    public async Task UninstallAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigurationPath))
        {
            return;
        }

        var root = JsonNode.Parse(await File.ReadAllTextAsync(ConfigurationPath, cancellationToken)) as JsonObject
            ?? throw new InvalidOperationException("The existing Antigravity hooks file is invalid.");
        if (!RemoveOwnedHandlers(root))
        {
            return;
        }

        await WriteAsync(root, cancellationToken);
    }

    private async Task WriteAsync(JsonObject root, CancellationToken cancellationToken)
    {
        var temporaryPath = ConfigurationPath + ".ai-usage-monitor.tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            cancellationToken);
        File.Move(temporaryPath, ConfigurationPath, overwrite: true);
    }

    private static IEnumerable<JsonObject> FindOwnedHandlers(JsonObject root)
    {
        foreach (var definition in root.Select(property => property.Value).OfType<JsonObject>())
        {
            if (definition["Stop"] is not JsonArray handlers)
            {
                continue;
            }

            foreach (var handler in handlers.OfType<JsonObject>().Where(IsOwnedHandler))
            {
                yield return handler;
            }
        }
    }

    private static bool RemoveOwnedHandlers(JsonObject root)
    {
        var removed = false;
        foreach (var property in root.ToList())
        {
            if (property.Value is not JsonObject definition || definition["Stop"] is not JsonArray handlers)
            {
                continue;
            }

            for (var index = handlers.Count - 1; index >= 0; index--)
            {
                if (handlers[index] is JsonObject handler && IsOwnedHandler(handler))
                {
                    handlers.RemoveAt(index);
                    removed = true;
                }
            }

            if (property.Key == OwnedHookName && handlers.Count == 0)
            {
                root.Remove(property.Key);
            }
        }

        return removed;
    }

    private static bool IsOwnedHandler(JsonObject handler)
    {
        var command = GetCommand(handler);
        if (command is null)
        {
            return false;
        }

        return command.Contains(
                   $"{HookProtocol.OwnerSwitch} {HookProtocol.OwnerId}",
                   StringComparison.OrdinalIgnoreCase) &&
               command.Contains(
                   $"{HookProtocol.NotifySwitch} {NotifyArgument}",
                   StringComparison.OrdinalIgnoreCase) ||
               HookProtocol.IsLegacyExecutable(command) &&
               command.Contains(
                   $"{HookProtocol.NotifySwitch} {NotifyArgument}",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetCommand(JsonObject handler) =>
        handler["command"] is JsonValue value && value.TryGetValue<string>(out var command)
            ? command
            : null;
}
