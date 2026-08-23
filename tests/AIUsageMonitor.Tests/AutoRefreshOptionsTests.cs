using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class AutoRefreshOptionsTests
{
    [TestMethod]
    public void GetScheduledInterval_UsesProviderIntervalWhileComputerIsActive_AndIdleIntervalAfterThreshold()
    {
        var options = new AutoRefreshOptions();
        options.Update(
            enabled: true,
            codexIntervalMinutes: 15,
            claudeIntervalMinutes: 20,
            antigravityIntervalMinutes: 20,
            cursorIntervalMinutes: 5,
            idleAfterMinutes: 10,
            idleRefreshIntervalMinutes: 60);

        Assert.AreEqual(TimeSpan.FromMinutes(15), options.GetScheduledInterval(ProviderKind.Codex, TimeSpan.FromMinutes(9)));
        Assert.AreEqual(TimeSpan.FromMinutes(60), options.GetScheduledInterval(ProviderKind.Codex, TimeSpan.FromMinutes(10)));
        Assert.IsTrue(options.IsComputerIdle(TimeSpan.FromMinutes(10)));
    }
}
