using System.Text.Json;
using AIUsageMonitor.Models;
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

}
