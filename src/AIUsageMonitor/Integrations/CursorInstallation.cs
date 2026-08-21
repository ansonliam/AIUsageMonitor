using System.IO;

namespace AIUsageMonitor.Integrations;

public static class CursorInstallation
{
    public static string ConfigurationDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cursor");

    public static string HooksPath => Path.Combine(ConfigurationDirectory, "hooks.json");

    public static bool IsDetected() =>
        File.Exists(GetExecutablePath()) || Directory.Exists(ConfigurationDirectory);

    private static string GetExecutablePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "cursor",
        "Cursor.exe");
}
