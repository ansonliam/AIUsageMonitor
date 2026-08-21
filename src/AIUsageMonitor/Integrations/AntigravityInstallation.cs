using System.IO;

namespace AIUsageMonitor.Integrations;

public static class AntigravityInstallation
{
    public static string ConfigurationDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".gemini",
        "config");

    public static string HooksPath => Path.Combine(ConfigurationDirectory, "hooks.json");

    public static bool IsDetected() =>
        GetExecutableCandidates().Any(File.Exists) ||
        Directory.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini",
            "antigravity")) ||
        Directory.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini",
            "antigravity-cli"));

    public static IEnumerable<string> GetExecutableCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(localAppData, "Programs", "Antigravity", "Antigravity.exe");
        yield return Path.Combine(localAppData, "Antigravity", "Antigravity.exe");
        yield return Path.Combine(programFiles, "Antigravity", "Antigravity.exe");
        yield return Path.Combine(localAppData, "agy", "bin", "agy.exe");

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim(), "agy.exe");
            }
            catch (ArgumentException)
            {
                continue;
            }

            yield return candidate;
        }
    }
}
