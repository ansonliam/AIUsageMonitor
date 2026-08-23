using System.Text.Json;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class ExistingProviderRegressionTests
{
    [TestMethod]
    public void UsageMetric_StillConvertsRemainingToUsedPercentage()
    {
        var metric = new UsageMetricViewModel("W");

        metric.SetUsage(30, null);

        Assert.AreEqual(70d, metric.UsedPercent!.Value, 0.001);
        Assert.AreEqual("70%", metric.PercentText);
        Assert.AreEqual(70d, metric.ProgressValue, 0.001);
    }

    [TestMethod]
    public void UsageSnapshot_OldCacheWithoutDynamicWindows_StillDeserializes()
    {
        var snapshot = JsonSerializer.Deserialize<UsageSnapshot>("""
            {
              "Provider": "Claude Code",
              "FiveHourRemainingPercent": 75,
              "WeeklyRemainingPercent": 60,
              "RetrievedAt": "2026-08-21T12:00:00+10:00",
              "Status": 0
            }
            """);

        Assert.IsNotNull(snapshot);
        Assert.IsEmpty(snapshot.Windows);
        Assert.AreEqual(75d, snapshot.FiveHourRemainingPercent);
        Assert.AreEqual(60d, snapshot.WeeklyRemainingPercent);
    }

    [TestMethod]
    public void AutoRefreshOptions_KeepsIndependentProviderIntervals()
    {
        var options = new AutoRefreshOptions();

        options.Update(true, 15, 20, 25, 30);

        Assert.AreEqual(TimeSpan.FromMinutes(15), options.GetInterval(ProviderKind.Codex));
        Assert.AreEqual(TimeSpan.FromMinutes(20), options.GetInterval(ProviderKind.Claude));
        Assert.AreEqual(TimeSpan.FromMinutes(25), options.GetInterval(ProviderKind.Antigravity));
        Assert.AreEqual(TimeSpan.FromMinutes(30), options.GetInterval(ProviderKind.Cursor));
    }

    [TestMethod]
    public void AutoRefreshOptions_ZeroThrottleAllowsImmediateRefresh()
    {
        var options = new AutoRefreshOptions();

        options.UpdateThrottle(0, 0, 0, 0);

        Assert.AreEqual(TimeSpan.Zero, options.GetThrottleInterval(ProviderKind.Codex));
        Assert.AreEqual(TimeSpan.Zero, options.GetThrottleInterval(ProviderKind.Claude));
        Assert.AreEqual(TimeSpan.Zero, options.GetThrottleInterval(ProviderKind.Antigravity));
        Assert.AreEqual(TimeSpan.Zero, options.GetThrottleInterval(ProviderKind.Cursor));
    }
}
