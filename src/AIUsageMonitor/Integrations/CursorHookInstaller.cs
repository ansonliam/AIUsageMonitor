using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIUsageMonitor.Integrations;

public sealed class CursorHookInstaller
{
    private const string NotifyArgument = "cursor";
    private const string HookEventName = "stop";

    public string ConfigurationPath => CursorInstallation.HooksPath;

    public HookInstallationStatus GetStatus()
    {
        if (!CursorInstallation.IsDetected())
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
        Directory.CreateDirectory(CursorInstallation.ConfigurationDirectory);
        JsonObject root;
        if (File.Exists(ConfigurationPath))
        {
            root = JsonNode.Parse(await File.ReadAllTextAsync(ConfigurationPath, cancellationToken)) as JsonObject
                ?? throw new InvalidOperationException("The existing Cursor hooks file is invalid.");
        }
        else
        {
            root = new JsonObject();
        }

        if (root["version"] is null)
        {
            root["version"] = 1;
        }

        var hooks = root["hooks"] as JsonObject;
        if (root["hooks"] is not null && hooks is null)
        {
            throw new InvalidOperationException("The existing Cursor hooks configuration is invalid.");
        }

        hooks ??= new JsonObject();
        root["hooks"] = hooks;

        var stopHandlers = hooks[HookEventName] as JsonArray;
        if (hooks[HookEventName] is not null && stopHandlers is null)
        {
            throw new InvalidOperationException("The existing Cursor stop hooks are invalid.");
        }

        stopHandlers ??= new JsonArray();
        hooks[HookEventName] = stopHandlers;
        RemoveOwnedHandlers(stopHandlers);

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The application path could not be determined.");
        var command = $"\"{executable}\" {string.Join(' ', HookProtocol.CreateArguments(NotifyArgument))}";
        stopHandlers.Add(new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["timeout"] = 10
        });

        await WriteAsync(root, cancellationToken);
    }

    public async Task UninstallAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigurationPath))
        {
            return;
        }

        var root = JsonNode.Parse(await File.ReadAllTextAsync(ConfigurationPath, cancellationToken)) as JsonObject
            ?? throw new InvalidOperationException("The existing Cursor hooks file is invalid.");
        if (root["hooks"] is not JsonObject hooks || hooks[HookEventName] is not JsonArray stopHandlers)
        {
            return;
        }

        if (!RemoveOwnedHandlers(stopHandlers))
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
        if (root["hooks"] is not JsonObject hooks || hooks[HookEventName] is not JsonArray handlers)
        {
            yield break;
        }

        foreach (var handler in handlers.OfType<JsonObject>().Where(IsOwnedHandler))
        {
            yield return handler;
        }
    }

    private static bool RemoveOwnedHandlers(JsonArray handlers)
    {
        var removed = false;
        for (var index = handlers.Count - 1; index >= 0; index--)
        {
            if (handlers[index] is JsonObject handler && IsOwnedHandler(handler))
            {
                handlers.RemoveAt(index);
                removed = true;
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

        var uniqueMarker = $"{HookProtocol.OwnerSwitch} {HookProtocol.OwnerId}";
        if (command.Contains(uniqueMarker, StringComparison.OrdinalIgnoreCase))
        {
            return command.Contains(
                $"{HookProtocol.NotifySwitch} {NotifyArgument}",
                StringComparison.OrdinalIgnoreCase);
        }

        return HookProtocol.IsLegacyExecutable(command) &&
               command.Contains(
                   $"{HookProtocol.NotifySwitch} {NotifyArgument}",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetCommand(JsonObject handler) =>
        handler["command"] is JsonValue value && value.TryGetValue<string>(out var command)
            ? command
            : null;
}
