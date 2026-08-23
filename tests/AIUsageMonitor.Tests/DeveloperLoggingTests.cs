using AIUsageMonitor.Services;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;

namespace AIUsageMonitor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DeveloperLoggingTests
{
    [TestMethod]
    public void DeveloperMode_PersistsAndRoutesRefreshActivityToNamedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        var logDirectory = Path.Combine(root, "logs");
        try
        {
            var store = new DeveloperModeSettingsStore(root);
            using var logging = new DeveloperLoggingService(store, logDirectory);
            using var loggerFactory = LoggerFactory.Create(builder => builder
                .AddNLog()
                .SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information));
            var logger = loggerFactory.CreateLogger<UsageRefreshService>();

            Assert.IsFalse(logging.IsEnabled);
            Assert.IsTrue(logging.TrySetEnabled(true));
            Assert.IsTrue(new DeveloperModeSettingsStore(root).LoadEnabled());

            logger.LogInformation(
                "Refresh requested | Provider={Provider} | API={Api} | Trigger={Trigger}",
                "Google Antigravity",
                "RetrieveUserQuotaSummary RPC",
                "Hook");
            loggerFactory.CreateLogger("AIUsageMonitor.Providers.CodexUsageProvider")
                .LogInformation("Provider API refresh completed | Provider=OpenAI Codex");
            loggerFactory.CreateLogger("AIUsageMonitor.Providers.ClaudeUsageProvider")
                .LogInformation("Provider API refresh completed | Provider=Claude Code");
            loggerFactory.CreateLogger("AIUsageMonitor.Providers.AntigravityUsageProvider")
                .LogInformation("Provider API refresh completed | Provider=Google Antigravity");
            loggerFactory.CreateLogger("AIUsageMonitor.Providers.CursorUsageProvider")
                .LogInformation("Provider API refresh completed | Provider=Cursor");
            loggerFactory.CreateLogger("AIUsageMonitor.Services.CodexApiCostService")
                .LogInformation("API cost scan completed | Providers=OpenAI Codex,Claude Code");
            NLog.LogManager.Flush(TimeSpan.FromSeconds(2));

            var applicationLog = Path.Combine(logDirectory, "application.log");
            var refreshLog = Path.Combine(logDirectory, "refresh-activity.log");
            Assert.IsTrue(File.Exists(applicationLog));
            Assert.IsTrue(File.Exists(refreshLog));
            StringAssert.Contains(File.ReadAllText(refreshLog), "Provider=Google Antigravity");
            StringAssert.Contains(File.ReadAllText(refreshLog), "Trigger=Hook");
            Assert.IsTrue(File.Exists(Path.Combine(
                logDirectory,
                "providers",
                "openai-codex__app-server-rate-limits.log")));
            Assert.IsTrue(File.Exists(Path.Combine(
                logDirectory,
                "providers",
                "claude-code__oauth-usage-api.log")));
            Assert.IsTrue(File.Exists(Path.Combine(
                logDirectory,
                "providers",
                "google-antigravity__retrieve-user-quota-summary-rpc.log")));
            Assert.IsTrue(File.Exists(Path.Combine(
                logDirectory,
                "providers",
                "cursor__usage-summary-api.log")));
            Assert.IsTrue(File.Exists(Path.Combine(
                logDirectory,
                "api-cost",
                "codex-and-claude__local-session-cost-scan.log")));

            Assert.IsTrue(logging.TrySetEnabled(false));
            Assert.IsFalse(new DeveloperModeSettingsStore(root).LoadEnabled());
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
