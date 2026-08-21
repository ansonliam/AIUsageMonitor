using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;

namespace AIUsageMonitor.Integrations;

public sealed class CodexHookInstaller
{
    private const string NotifyArgument = "codex";
    private readonly string _codexDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex");

    public string ConfigurationPath => Path.Combine(_codexDirectory, "hooks.json");

    public HookInstallationStatus GetStatus()
    {
        if (!Directory.Exists(_codexDirectory))
        {
            return HookInstallationStatus.ClientNotDetected;
        }

        var hooksPath = ConfigurationPath;
        if (!File.Exists(hooksPath))
        {
            return HookInstallationStatus.NotInstalled;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(hooksPath));
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
        Directory.CreateDirectory(_codexDirectory);
        var hooksPath = ConfigurationPath;
        JsonObject root;

        if (File.Exists(hooksPath))
        {
            var existing = await File.ReadAllTextAsync(hooksPath, cancellationToken);
            root = JsonNode.Parse(existing) as JsonObject
                ?? throw new InvalidOperationException("The existing Codex hooks file is invalid.");
        }
        else
        {
            root = new JsonObject();
        }

        var hooks = root["hooks"] as JsonObject;
        if (root["hooks"] is not null && hooks is null)
        {
            throw new InvalidOperationException("The existing Codex hooks configuration is invalid.");
        }
        hooks ??= new JsonObject();
        root["hooks"] = hooks;

        var stopGroups = hooks["Stop"] as JsonArray;
        if (hooks["Stop"] is not null && stopGroups is null)
        {
            throw new InvalidOperationException("The existing Codex Stop hooks are invalid.");
        }
        stopGroups ??= new JsonArray();
        hooks["Stop"] = stopGroups;

        RemoveOwnedHandlers(stopGroups);
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The application path could not be determined.");
        var command = $"\"{executable}\" {string.Join(' ', HookProtocol.CreateArguments(NotifyArgument))}";
        stopGroups.Add(new JsonObject
        {
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["commandWindows"] = command,
                    ["async"] = true,
                    ["timeout"] = 10
                }
            }
        });

        var options = new JsonSerializerOptions { WriteIndented = true };
        var tempPath = hooksPath + ".ai-usage-monitor.tmp";
        await File.WriteAllTextAsync(tempPath, root.ToJsonString(options) + Environment.NewLine, cancellationToken);
        File.Move(tempPath, hooksPath, overwrite: true);
    }

    public async Task UninstallAsync(CancellationToken cancellationToken = default)
    {
        var hooksPath = ConfigurationPath;
        if (!File.Exists(hooksPath))
        {
            return;
        }

        var existing = await File.ReadAllTextAsync(hooksPath, cancellationToken);
        var root = JsonNode.Parse(existing) as JsonObject
            ?? throw new InvalidOperationException("The existing Codex hooks file is invalid.");
        if (root["hooks"] is null)
        {
            return;
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            throw new InvalidOperationException("The existing Codex hooks configuration is invalid.");
        }

        if (hooks["Stop"] is null)
        {
            return;
        }

        if (hooks["Stop"] is not JsonArray stopGroups)
        {
            throw new InvalidOperationException("The existing Codex Stop hooks are invalid.");
        }

        if (!RemoveOwnedHandlers(stopGroups))
        {
            return;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        var temporaryPath = hooksPath + ".ai-usage-monitor.tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            root.ToJsonString(options) + Environment.NewLine,
            cancellationToken);
        File.Move(temporaryPath, hooksPath, overwrite: true);
    }

    private static IEnumerable<JsonObject> FindOwnedHandlers(JsonNode? root)
    {
        if (root?["hooks"]?["Stop"] is not JsonArray groups)
        {
            yield break;
        }

        foreach (var group in groups.OfType<JsonObject>())
        {
            if (group["hooks"] is not JsonArray handlers)
            {
                continue;
            }

            foreach (var handler in handlers.OfType<JsonObject>())
            {
                if (IsOwnedHandler(handler))
                {
                    yield return handler;
                }
            }
        }
    }

    private static bool RemoveOwnedHandlers(JsonArray groups)
    {
        var removed = false;
        for (var groupIndex = groups.Count - 1; groupIndex >= 0; groupIndex--)
        {
            if (groups[groupIndex] is not JsonObject group || group["hooks"] is not JsonArray handlers)
            {
                continue;
            }

            for (var handlerIndex = handlers.Count - 1; handlerIndex >= 0; handlerIndex--)
            {
                if (handlers[handlerIndex] is not JsonObject handler)
                {
                    continue;
                }

                if (IsOwnedHandler(handler))
                {
                    handlers.RemoveAt(handlerIndex);
                    removed = true;
                }
            }

            if (handlers.Count == 0)
            {
                groups.RemoveAt(groupIndex);
            }
        }

        return removed;
    }

    private static string? GetCommand(JsonObject handler)
    {
        foreach (var propertyName in new[] { "commandWindows", "command" })
        {
            if (handler[propertyName] is JsonValue value && value.TryGetValue<string>(out var command))
            {
                return command;
            }
        }

        return null;
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
}
