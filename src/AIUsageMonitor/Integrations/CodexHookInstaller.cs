using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using System.IO;

namespace AIUsageMonitor.Integrations;

public sealed class CodexHookInstaller
{
    private const string NotifyArgument = "codex";
    private const string LauncherFileName = "codex-notify.cmd";
    private readonly string _codexDirectory;
    private readonly string _launcherDirectory;

    public CodexHookInstaller()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIUsageMonitor",
                "hooks"))
    {
    }

    internal CodexHookInstaller(string codexDirectory, string launcherDirectory)
    {
        _codexDirectory = codexDirectory;
        _launcherDirectory = launcherDirectory;
    }

    public string ConfigurationPath => Path.Combine(_codexDirectory, "hooks.json");
    internal string WindowsLauncherPath => Path.Combine(_launcherDirectory, LauncherFileName);

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
        await WriteWindowsLauncherAsync(executable, cancellationToken);
        stopGroups.Add(new JsonObject
        {
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["commandWindows"] = BuildWindowsCommand(),
                    ["async"] = false,
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
        DeleteWindowsLauncher();
    }

    private IEnumerable<JsonObject> FindOwnedHandlers(JsonNode? root)
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

    private bool RemoveOwnedHandlers(JsonArray groups)
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

    private bool IsCurrentHandler(JsonObject handler, string executable)
    {
        var command = GetCommand(handler);
        if (command is null || !File.Exists(WindowsLauncherPath))
        {
            return false;
        }

        if (!string.Equals(GetWindowsCommand(handler), BuildWindowsCommand(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedLauncher = BuildWindowsLauncherContents(executable);
        return string.Equals(File.ReadAllText(WindowsLauncherPath), expectedLauncher, StringComparison.Ordinal);
    }

    private async Task WriteWindowsLauncherAsync(string executable, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_launcherDirectory);
        var launcherPath = WindowsLauncherPath;
        var temporaryPath = launcherPath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            BuildWindowsLauncherContents(executable),
            cancellationToken);
        File.Move(temporaryPath, launcherPath, overwrite: true);
    }

    private void DeleteWindowsLauncher()
    {
        if (File.Exists(WindowsLauncherPath))
        {
            File.Delete(WindowsLauncherPath);
        }
    }

    private string BuildWindowsCommand() =>
        $"cmd.exe /c {GetQuoteFreePath(WindowsLauncherPath)}";

    internal static string BuildWindowsLauncherContents(string executable) =>
        $"@echo off{Environment.NewLine}\"{executable}\" {string.Join(' ', HookProtocol.CreateArguments(NotifyArgument))}{Environment.NewLine}exit /b %ERRORLEVEL%{Environment.NewLine}";

    private static string GetQuoteFreePath(string path)
    {
        if (!path.Contains(' ', StringComparison.Ordinal))
        {
            return path;
        }

        var buffer = new char[512];
        var length = GetShortPathName(path, buffer, buffer.Length);
        if (length > 0 && length < buffer.Length)
        {
            var shortPath = new string(buffer, 0, (int)length);
            if (!shortPath.Contains(' ', StringComparison.Ordinal))
            {
                return shortPath;
            }
        }

        throw new InvalidOperationException(
            "Codex needs a quote-free Windows hook launcher path, but Windows did not provide one.");
    }

    private static string? GetWindowsCommand(JsonObject handler) =>
        handler["commandWindows"] is JsonValue value && value.TryGetValue<string>(out var command)
            ? command
            : null;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, char[] shortPath, int bufferLength);

    private bool IsOwnedHandler(JsonObject handler)
    {
        if (File.Exists(WindowsLauncherPath) &&
            string.Equals(GetWindowsCommand(handler), BuildWindowsCommand(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

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
