using AIUsageMonitor.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class ClaudeThirdPartyRoutingMonitorTests
{
    [TestMethod]
    public void FirstPartyInstall_IsNotThirdPartyRouted()
    {
        using var directory = new TemporaryClaudeDirectory();
        directory.WriteSettings("{\"env\":{}}");

        using var monitor = directory.CreateMonitor();

        Assert.IsFalse(monitor.IsThirdPartyRouted);
    }

    [TestMethod]
    public void BedrockRoutingInSettings_IsThirdPartyRouted()
    {
        using var directory = new TemporaryClaudeDirectory();
        directory.WriteSettings("{\"env\":{\"CLAUDE_CODE_USE_BEDROCK\":\"1\",\"AWS_REGION\":\"us-east-1\"}}");

        using var monitor = directory.CreateMonitor();

        Assert.IsTrue(monitor.IsThirdPartyRouted);
    }

    [TestMethod]
    public void BedrockRoutingInEnvironment_IsThirdPartyRouted()
    {
        using var directory = new TemporaryClaudeDirectory();
        directory.WriteSettings("{}");

        using var monitor = directory.CreateMonitor(
            key => key == "CLAUDE_CODE_USE_BEDROCK" ? "1" : null);

        Assert.IsTrue(monitor.IsThirdPartyRouted);
    }

    [TestMethod]
    public void MissingSettingsFile_FailsClosed()
    {
        using var directory = new TemporaryClaudeDirectory();

        using var monitor = directory.CreateMonitor();

        Assert.IsFalse(monitor.IsThirdPartyRouted);
    }

    [TestMethod]
    public async Task SettingsFileChange_UpdatesRoutingState()
    {
        using var directory = new TemporaryClaudeDirectory();
        directory.WriteSettings("{\"env\":{}}");
        using var monitor = directory.CreateMonitor();
        Assert.IsFalse(monitor.IsThirdPartyRouted);

        var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.RoutingChanged += value => changed.TrySetResult(value);
        directory.WriteSettings("{\"env\":{\"CLAUDE_CODE_USE_MANTLE\":\"true\"}}");

        Assert.IsTrue(await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(monitor.IsThirdPartyRouted);
    }

    private sealed class TemporaryClaudeDirectory : IDisposable
    {
        public TemporaryClaudeDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ai-usage-claude-routing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void WriteSettings(string json) =>
            File.WriteAllText(System.IO.Path.Combine(Path, "settings.json"), json);

        public ClaudeThirdPartyRoutingMonitor CreateMonitor(
            Func<string, string?>? getEnvironmentVariable = null) =>
            new(
                Path,
                NullLogger<ClaudeThirdPartyRoutingMonitor>.Instance,
                getEnvironmentVariable ?? (_ => null));

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
