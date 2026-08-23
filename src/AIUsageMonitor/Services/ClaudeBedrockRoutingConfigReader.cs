using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIUsageMonitor.Authentication;

namespace AIUsageMonitor.Services;

// Whether this machine's Claude Code install is currently routed through AWS Bedrock, and which
// region to attribute usage to. This is a config-level fact - Claude Code's JSONL transcripts
// never record which backend served a given request (see ClaudeSessionLogScanner) - so it has to
// come from settings.json/environment instead of anything in the logs themselves.
public sealed record ClaudeBedrockRoutingConfig(bool IsActive, string Region);

public static partial class ClaudeBedrockRoutingConfigReader
{
    [GeneratedRegex(@"bedrock-mantle\.([a-z0-9-]+)\.api\.aws", RegexOptions.IgnoreCase)]
    private static partial Regex MantleRegionPattern();

    // Both routing modes are configured the same way in practice: either directly in
    // ~/.claude/settings.json's "env" block (Claude Code applies that block to its own process
    // env on launch), or as plain OS environment variables (e.g. set in a shell profile). Settings
    // file values take precedence when both are present, since they're the more deliberate,
    // inspectable source of truth.
    public static ClaudeBedrockRoutingConfig Read(
        string? claudeConfigDirectory = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        var envLookup = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var settingsEnv = ReadSettingsEnvBlock(claudeConfigDirectory ?? ClaudeAuthentication.GetClaudeDirectory());

        string? GetSetting(string key) =>
            settingsEnv.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
                ? value
                : envLookup(key);

        if (IsTruthy(GetSetting("CLAUDE_CODE_USE_BEDROCK")))
        {
            var region = GetSetting("AWS_REGION")?.Trim().ToLowerInvariant() ?? "";
            return new ClaudeBedrockRoutingConfig(IsActive: true, Region: region);
        }

        if (IsTruthy(GetSetting("CLAUDE_CODE_USE_MANTLE")))
        {
            var mantleBaseUrl = GetSetting("ANTHROPIC_BEDROCK_MANTLE_BASE_URL");
            var region = mantleBaseUrl is not null && MantleRegionPattern().Match(mantleBaseUrl) is { Success: true } match
                ? match.Groups[1].Value.ToLowerInvariant()
                : "";
            return new ClaudeBedrockRoutingConfig(IsActive: true, Region: region);
        }

        return new ClaudeBedrockRoutingConfig(IsActive: false, Region: "");
    }

    private static Dictionary<string, string?> ReadSettingsEnvBlock(string claudeConfigDirectory)
    {
        var result = new Dictionary<string, string?>();
        var settingsPath = Path.Combine(claudeConfigDirectory, "settings.json");
        if (!File.Exists(settingsPath))
        {
            return result;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(settingsPath));
            if (root?["env"] is not JsonObject env)
            {
                return result;
            }

            foreach (var property in env)
            {
                result[property.Key] = property.Value is JsonValue value && value.TryGetValue<string>(out var text)
                    ? text
                    : null;
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return result;
    }

    private static bool IsTruthy(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value != "0" &&
        !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
}
