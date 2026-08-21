namespace AIUsageMonitor.Integrations;

internal static class HookProtocol
{
    public const string OwnerSwitch = "--hook-owner";
    public const string OwnerId = "com.ansonliam.ai-usage-monitor";
    public const string NotifySwitch = "--notify";

    public static string[] CreateArguments(string provider) =>
        [OwnerSwitch, OwnerId, NotifySwitch, provider];

    public static bool IsOwnedArguments(IReadOnlyList<string?> arguments, string provider) =>
        arguments.Count == 4 &&
        string.Equals(arguments[0], OwnerSwitch, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(arguments[1], OwnerId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(arguments[2], NotifySwitch, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(arguments[3], provider, StringComparison.OrdinalIgnoreCase);

    public static bool IsLegacyArguments(IReadOnlyList<string?> arguments, string provider) =>
        arguments.Count == 2 &&
        string.Equals(arguments[0], NotifySwitch, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(arguments[1], provider, StringComparison.OrdinalIgnoreCase);

    public static bool TryReadNotification(string[] arguments, out string provider)
    {
        provider = string.Empty;
        if (arguments.Length == 4 &&
            string.Equals(arguments[0], OwnerSwitch, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(arguments[1], OwnerId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(arguments[2], NotifySwitch, StringComparison.OrdinalIgnoreCase))
        {
            provider = arguments[3].Trim().ToLowerInvariant();
            return provider is "codex" or "claude" or "antigravity";
        }

        if (arguments.Length == 2 &&
            string.Equals(arguments[0], NotifySwitch, StringComparison.OrdinalIgnoreCase))
        {
            provider = arguments[1].Trim().ToLowerInvariant();
            return provider is "codex" or "claude" or "antigravity";
        }

        return false;
    }

    public static bool IsLegacyExecutable(string? command) =>
        command?.Contains("AIUsageMonitor.exe", StringComparison.OrdinalIgnoreCase) == true;
}
