using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using AIUsageMonitor.Authentication;

namespace AIUsageMonitor.Integrations;

public sealed class ClaudeHookInstaller
{
    private const string NotifyArgument = "claude";

    public string ConfigurationPath => Path.Combine(ClaudeAuthentication.GetClaudeDirectory(), "settings.json");

    public HookInstallationStatus GetStatus()
    {
        var claudeDirectory = ClaudeAuthentication.GetClaudeDirectory();
        if (!Directory.Exists(claudeDirectory) &&
            !File.Exists(ClaudeAuthentication.FindClaudeExecutable()))
        {
            return HookInstallationStatus.ClientNotDetected;
        }

        var settingsPath = ConfigurationPath;
        if (!File.Exists(settingsPath))
        {
            return HookInstallationStatus.NotInstalled;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(settingsPath));
            var handlers = FindOwnedHandlers(root).ToList();
            if (handlers.Count == 0)
            {
                return HookInstallationStatus.NotInstalled;
            }

            var executable = Environment.ProcessPath;
            return handlers.Count == 1 &&
                   executable is not null &&
                   IsCurrentHandler(handlers[0], executable)
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
        var claudeDirectory = ClaudeAuthentication.GetClaudeDirectory();
        Directory.CreateDirectory(claudeDirectory);
        var settingsPath = ConfigurationPath;
        JsonObject root;

        if (File.Exists(settingsPath))
        {
            root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath, cancellationToken)) as JsonObject
                ?? throw new InvalidOperationException("The existing Claude settings file is invalid.");
        }
        else
        {
            root = new JsonObject();
        }

        var hooks = root["hooks"] as JsonObject;
        if (root["hooks"] is not null && hooks is null)
        {
            throw new InvalidOperationException("The existing Claude hooks configuration is invalid.");
        }

        hooks ??= new JsonObject();
        root["hooks"] = hooks;
        var stopGroups = hooks["Stop"] as JsonArray;
        if (hooks["Stop"] is not null && stopGroups is null)
        {
            throw new InvalidOperationException("The existing Claude Stop hooks are invalid.");
        }

        stopGroups ??= new JsonArray();
        hooks["Stop"] = stopGroups;
        RemoveOwnedHandlers(stopGroups);

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The application path could not be determined.");
        var arguments = HookProtocol.CreateArguments(NotifyArgument);
        stopGroups.Add(new JsonObject
        {
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = executable,
                    ["args"] = new JsonArray(
                        arguments.Select(argument => (JsonNode?)JsonValue.Create(argument)).ToArray()),
                    ["async"] = true,
                    ["timeout"] = 10
                }
            }
        });

        var options = new JsonSerializerOptions { WriteIndented = true };
        var temporaryPath = settingsPath + ".ai-usage-monitor.tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            root.ToJsonString(options) + Environment.NewLine,
            cancellationToken);
        File.Move(temporaryPath, settingsPath, overwrite: true);
    }

    public async Task UninstallAsync(CancellationToken cancellationToken = default)
    {
        var settingsPath = ConfigurationPath;
        if (!File.Exists(settingsPath))
        {
            return;
        }

        var existing = await File.ReadAllTextAsync(settingsPath, cancellationToken);
        var root = JsonNode.Parse(existing) as JsonObject
            ?? throw new InvalidOperationException("The existing Claude settings file is invalid.");
        if (root["hooks"] is null)
        {
            return;
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            throw new InvalidOperationException("The existing Claude hooks configuration is invalid.");
        }

        if (hooks["Stop"] is null)
        {
            return;
        }

        if (hooks["Stop"] is not JsonArray stopGroups)
        {
            throw new InvalidOperationException("The existing Claude Stop hooks are invalid.");
        }

        if (!RemoveOwnedHandlers(stopGroups))
        {
            return;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        var temporaryPath = settingsPath + ".ai-usage-monitor.tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            root.ToJsonString(options) + Environment.NewLine,
            cancellationToken);
        File.Move(temporaryPath, settingsPath, overwrite: true);
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
                if (handlers[handlerIndex] is JsonObject handler && IsOwnedHandler(handler))
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

    private static bool IsOwnedHandler(JsonObject handler)
    {
        var command = ReadString(handler["command"]);
        if (handler["args"] is JsonArray args)
        {
            var values = args.Select(ReadString).ToArray();
            return HookProtocol.IsOwnedArguments(values, NotifyArgument) ||
                   HookProtocol.IsLegacyExecutable(command) &&
                   HookProtocol.IsLegacyArguments(values, NotifyArgument);
        }

        var uniqueMarker = $"{HookProtocol.OwnerSwitch} {HookProtocol.OwnerId}";
        return command?.Contains(uniqueMarker, StringComparison.OrdinalIgnoreCase) == true &&
               command.Contains(
                   $"{HookProtocol.NotifySwitch} {NotifyArgument}",
                   StringComparison.OrdinalIgnoreCase) ||
               HookProtocol.IsLegacyExecutable(command) &&
               command!.Contains(
                   $"{HookProtocol.NotifySwitch} {NotifyArgument}",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurrentHandler(JsonObject handler, string executable) =>
        string.Equals(ReadString(handler["command"]), executable, StringComparison.OrdinalIgnoreCase) &&
        handler["args"] is JsonArray args &&
        HookProtocol.IsOwnedArguments(args.Select(ReadString).ToArray(), NotifyArgument);

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
