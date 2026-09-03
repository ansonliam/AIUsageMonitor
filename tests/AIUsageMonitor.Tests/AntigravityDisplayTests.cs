using AIUsageMonitor.Models;
using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class AntigravityDisplayTests
{
    [TestMethod]
    public void HideModels_AppliesBeforeRefreshAndCanRestoreBothPeriodsWithoutFetching()
    {
        var provider = new ProviderViewModel(ProviderKind.Antigravity, "Antigravity", null!);
        provider.SetHideAntigravityClaudeAndGptModels(true);
        provider.SetUsageDisplayMode(true);
        provider.ApplySnapshot(Snapshot());

        CollectionAssert.AreEqual(new[] { "G5H", "GW" }, Labels(provider));
        CollectionAssert.AreEqual(new[] { "60%", "90%" }, Percents(provider));

        provider.SetHideAntigravityClaudeAndGptModels(false);
        CollectionAssert.AreEqual(new[] { "G5H", "GW", "C5H", "CW" }, Labels(provider));
        CollectionAssert.AreEqual(new[] { "60%", "90%", "20%", "80%" }, Percents(provider));

        provider.SetHideAntigravityClaudeAndGptModels(true);
        provider.ApplySnapshot(Snapshot());
        CollectionAssert.AreEqual(new[] { "G5H", "GW" }, Labels(provider));

        provider.SetHideAntigravityFiveHourLimits(true);
        CollectionAssert.AreEqual(new[] { "GW" }, Labels(provider));
        provider.SetHideAntigravityClaudeAndGptModels(false);
        CollectionAssert.AreEqual(new[] { "GW", "CW" }, Labels(provider));
        provider.ApplySnapshot(Snapshot());
        CollectionAssert.AreEqual(new[] { "GW", "CW" }, Labels(provider));
        provider.SetHideAntigravityFiveHourLimits(false);
        CollectionAssert.AreEqual(new[] { "G5H", "GW", "C5H", "CW" }, Labels(provider));
    }

    [TestMethod]
    public void HideModels_PreservesStaleStateAndUsedModeAfterFailedRefresh()
    {
        var provider = new ProviderViewModel(ProviderKind.Antigravity, "Antigravity", null!);
        provider.ApplySnapshot(Snapshot());
        provider.ApplySnapshot(new UsageSnapshot { Provider = "Google Antigravity", Status = UsageStatus.Error });

        provider.SetHideAntigravityClaudeAndGptModels(true);
        Assert.IsTrue(provider.UsageWindows.All(metric => metric.IsStale));
        CollectionAssert.AreEqual(new[] { "40%", "10%" }, Percents(provider));
        provider.SetHideAntigravityClaudeAndGptModels(false);
        Assert.IsTrue(provider.UsageWindows.All(metric => metric.IsStale));

        provider.ApplySnapshot(Snapshot());
        Assert.IsTrue(provider.UsageWindows.All(metric => !metric.IsStale));
    }

    [TestMethod]
    public void WeeklyOnlyResponse_ShowsUnavailableFiveHourSlotThenReplacesItWithReportedQuota()
    {
        var provider = new ProviderViewModel(ProviderKind.Antigravity, "Antigravity", null!);
        provider.ApplySnapshot(Snapshot() with
        {
            Windows = [Window("Gemini Models", "W", 90)]
        });

        CollectionAssert.AreEqual(new[] { "G5H", "GW" }, Labels(provider));
        CollectionAssert.AreEqual(new[] { "—", "10%" }, Percents(provider));
        Assert.IsNull(provider.UsageWindows[0].UsedPercent);
        Assert.IsNull(provider.UsageWindows[0].ResetSummary);

        provider.SetHideAntigravityFiveHourLimits(true);
        CollectionAssert.AreEqual(new[] { "GW" }, Labels(provider));
        provider.ApplySnapshot(Snapshot());
        CollectionAssert.AreEqual(new[] { "GW", "CW" }, Labels(provider));
        provider.SetHideAntigravityFiveHourLimits(false);

        provider.ApplySnapshot(Snapshot());
        CollectionAssert.AreEqual(new[] { "G5H", "GW", "C5H", "CW" }, Labels(provider));
        CollectionAssert.AreEqual(new[] { "40%", "10%", "80%", "20%" }, Percents(provider));
    }

    [TestMethod]
    public void HideModels_FiltersLegacyCachedLabelsAndSeparateGptGroup()
    {
        var provider = new ProviderViewModel(ProviderKind.Antigravity, "Antigravity", null!);
        provider.ApplySnapshot(Snapshot() with
        {
            Windows = [
                new UsageWindowSnapshot { Label = "Gemini Models", RemainingPercent = 90 },
                new UsageWindowSnapshot { Label = "Claude and GPT models", RemainingPercent = 80 },
                Window("ChatGPT models", "5H", 50),
                Window("GPT-OSS", "W", 70)]
        });

        provider.SetHideAntigravityClaudeAndGptModels(true);
        Assert.AreEqual("Gemini Models", provider.UsageWindows.Single().Label);

        provider.SetHideAntigravityClaudeAndGptModels(false);
        Assert.IsTrue(provider.UsageWindows.Any(metric => metric.Label.Contains("ChatGPT")));
        Assert.IsTrue(provider.UsageWindows.Any(metric => metric.Label.Contains("Claude")));
    }

    [TestMethod]
    public void HideModels_DoesNotAffectOtherProviders()
    {
        var provider = new ProviderViewModel(ProviderKind.Claude, "Claude", null!);
        provider.ApplySnapshot(new UsageSnapshot
        {
            Provider = "Claude Code", Status = UsageStatus.Available,
            FiveHourRemainingPercent = 60, WeeklyRemainingPercent = 80
        });

        provider.SetHideAntigravityClaudeAndGptModels(true);

        CollectionAssert.AreEqual(new[] { "5H", "W" }, Labels(provider));
        provider.SetHideAntigravityFiveHourLimits(true);
        CollectionAssert.AreEqual(new[] { "5H", "W" }, Labels(provider));
        CollectionAssert.AreEqual(new[] { "40%", "20%" }, Percents(provider));
    }

    private static UsageSnapshot Snapshot() => new()
    {
        Provider = "Google Antigravity", Status = UsageStatus.Available,
        Windows = [Window("Gemini Models", "5H", 60), Window("Gemini Models", "W", 90),
            Window("Claude and GPT models", "5H", 20), Window("Claude and GPT models", "W", 80)]
    };

    private static UsageWindowSnapshot Window(string group, string period, double remaining) => new()
    {
        Label = $"{group} · {period}", GroupName = group, WindowLabel = period,
        RemainingPercent = remaining, ResetAt = DateTimeOffset.Now.AddHours(period == "5H" ? 3 : 48)
    };

    private static string[] Labels(ProviderViewModel provider) =>
        provider.UsageWindows.Select(metric => metric.ShortLabel).ToArray();

    private static string[] Percents(ProviderViewModel provider) =>
        provider.UsageWindows.Select(metric => metric.PercentText).ToArray();
}
