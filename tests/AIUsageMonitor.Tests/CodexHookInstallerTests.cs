using AIUsageMonitor.Integrations;
using System.Text.Json.Nodes;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class CodexHookInstallerTests
{
    [TestMethod]
    public void BuildWindowsLauncherContents_QuotesTheMonitorPathInsideTheLauncher()
    {
        var executable = Path.Combine(
            Path.GetPathRoot(Environment.SystemDirectory)!,
            "Program Files",
            "AI Usage Monitor",
            "AIUsageMonitor.exe");

        var contents = CodexHookInstaller.BuildWindowsLauncherContents(executable);

        StringAssert.StartsWith(contents, "@echo off");
        StringAssert.Contains(contents, $"\"{executable}\" --hook-owner com.ansonliam.ai-usage-monitor --notify codex");
        StringAssert.Contains(contents, "exit /b %ERRORLEVEL%");
    }

    [TestMethod]
    public async Task InstallOrRepairAsync_UsesSynchronousQuoteFreeWindowsLauncher()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        var installer = new CodexHookInstaller(
            Path.Combine(root, "codex"),
            Path.Combine(root, "launcher"));

        try
        {
            await installer.InstallOrRepairAsync();

            var configuration = JsonNode.Parse(await File.ReadAllTextAsync(installer.ConfigurationPath));
            var handler = configuration!["hooks"]!["Stop"]![0]!["hooks"]![0]!;

            Assert.IsFalse(handler["async"]!.GetValue<bool>());
            StringAssert.StartsWith(handler["commandWindows"]!.GetValue<string>(), "cmd.exe /c ");
            Assert.IsTrue(File.Exists(installer.WindowsLauncherPath));

            await installer.UninstallAsync();
            Assert.IsFalse(File.Exists(installer.WindowsLauncherPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
