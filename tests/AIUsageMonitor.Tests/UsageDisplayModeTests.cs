using AIUsageMonitor.Converters;
using AIUsageMonitor.Models;
using AIUsageMonitor.ViewModels;
using System.Windows.Media;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class UsageDisplayModeTests
{
    [TestMethod]
    public void HexColourPreview_InvalidOrIncompleteText_IsTransparent()
    {
        var converter = new HexColorBrushConverter();

        Assert.AreSame(Brushes.Transparent, converter.Convert(string.Empty, typeof(Brush), null!, null!));
        Assert.AreSame(Brushes.Transparent, converter.Convert("#FF", typeof(Brush), null!, null!));
        Assert.AreSame(Brushes.Transparent, converter.Convert("#GGGGGG", typeof(Brush), null!, null!));
    }

    [TestMethod]
    public void HexColourPreview_ValidHexText_ReturnsItsColour()
    {
        var converter = new HexColorBrushConverter();

        var brush = (SolidColorBrush)converter.Convert("#2ECC71", typeof(Brush), null!, null!);

        Assert.AreEqual(Color.FromRgb(0x2E, 0xCC, 0x71), brush.Color);
    }

    [TestMethod]
    public void UsageMetric_DefaultsToUsedPercentDisplay()
    {
        var metric = new UsageMetricViewModel("W");

        metric.SetUsage(30, null);

        Assert.AreEqual("70%", metric.PercentText);
        Assert.AreEqual(70d, metric.ProgressValue, 0.001);
    }

    [TestMethod]
    public void UsageMetric_ShowRemaining_DisplaysRemainingPercentNotUsed()
    {
        var metric = new UsageMetricViewModel("W");
        metric.SetUsage(30, null);

        metric.SetShowRemaining(true);

        Assert.AreEqual("30%", metric.PercentText);
        Assert.AreEqual(30d, metric.ProgressValue, 0.001);
    }

    [TestMethod]
    public void UsageMetric_ShowRemaining_DoesNotChangeUnderlyingUsedPercent()
    {
        // UsedPercent is what the colour converter is bound to on both the main and taskbar
        // widgets - it must stay fixed to the real used% regardless of the display toggle, so
        // colour severity never flips just because the displayed number did.
        var metric = new UsageMetricViewModel("W");
        metric.SetUsage(30, null);

        metric.SetShowRemaining(true);

        Assert.AreEqual(70d, metric.UsedPercent!.Value, 0.001);
    }

    [TestMethod]
    public void UsageMetric_TogglingBackToUsed_RestoresOriginalDisplay()
    {
        var metric = new UsageMetricViewModel("W");
        metric.SetUsage(30, null);

        metric.SetShowRemaining(true);
        metric.SetShowRemaining(false);

        Assert.AreEqual("70%", metric.PercentText);
        Assert.AreEqual(70d, metric.ProgressValue, 0.001);
    }

    [TestMethod]
    public void UsageMetric_ShowRemaining_WithNoUsageYet_DisplaysUnavailable()
    {
        var metric = new UsageMetricViewModel("W");

        metric.SetShowRemaining(true);

        Assert.AreEqual("—", metric.PercentText);
        Assert.AreEqual(0d, metric.ProgressValue, 0.001);
    }

    [TestMethod]
    public void UsageMetric_ColourConverter_ProducesSameColourRegardlessOfDisplayMode()
    {
        // Simulates the taskbar/main widget Foreground binding, which always reads UsedPercent.
        var converter = new UsageColorConverter();
        var metric = new UsageMetricViewModel("W");
        metric.SetUsage(10, null); // 90% used -> should land in the critical stage either way

        var usedModeColor = converter.Convert(metric.UsedPercent!.Value, typeof(object), null!, null!);
        metric.SetShowRemaining(true);
        var remainingModeColor = converter.Convert(metric.UsedPercent!.Value, typeof(object), null!, null!);

        Assert.AreEqual(usedModeColor, remainingModeColor);
    }

    [TestMethod]
    public void ProviderViewModel_RebuiltDynamicWindows_KeepRemainingDisplayMode()
    {
        // Antigravity/Cursor replace their metric instances on every refresh. The display mode
        // has to survive that rebuild, or those providers revert to used% on the next refresh.
        var provider = new ProviderViewModel(ProviderKind.Cursor, "Cursor", null!);
        provider.SetUsageDisplayMode(showRemaining: true);

        provider.ApplySnapshot(new UsageSnapshot
        {
            Provider = "Cursor",
            Status = UsageStatus.Available,
            Windows = [new UsageWindowSnapshot { Label = "Usage", RemainingPercent = 30 }]
        });

        var metric = provider.UsageWindows.Single();
        Assert.AreEqual("30%", metric.PercentText);
        Assert.AreEqual(70d, metric.UsedPercent!.Value, 0.001);
    }

    [TestMethod]
    public void ProviderViewModel_RebuiltDynamicWindows_KeepUsedDisplayModeByDefault()
    {
        var provider = new ProviderViewModel(ProviderKind.Cursor, "Cursor", null!);

        provider.ApplySnapshot(new UsageSnapshot
        {
            Provider = "Cursor",
            Status = UsageStatus.Available,
            Windows = [new UsageWindowSnapshot { Label = "Usage", RemainingPercent = 30 }]
        });

        Assert.AreEqual("70%", provider.UsageWindows.Single().PercentText);
    }

    [TestMethod]
    public void ProviderViewModel_SetUsageDisplayMode_AppliesToFixedWindowMetrics()
    {
        var provider = new ProviderViewModel(ProviderKind.Claude, "Claude", null!);
        provider.FiveHourUsage.SetUsage(30, null);
        provider.WeeklyUsage.SetUsage(40, null);

        provider.SetUsageDisplayMode(showRemaining: true);

        Assert.AreEqual("30%", provider.FiveHourUsage.PercentText);
        Assert.AreEqual("40%", provider.WeeklyUsage.PercentText);
    }

    [TestMethod]
    public void CodexProvider_ExposesFiveHourAndWeeklyUsageMetrics()
    {
        var provider = new ProviderViewModel(ProviderKind.Codex, "OpenAI Codex", null!);

        CollectionAssert.AreEqual(new[] { "5H", "W" }, provider.UsageWindows.Select(metric => metric.Label).ToArray());
    }

    [TestMethod]
    public void CodexApiCostPanel_DefaultsToBudgetSpentDisplay()
    {
        var panel = new CodexApiCostPanelViewModel(Guid.NewGuid());

        panel.Update(BudgetSummary(spentPercent: 20));

        Assert.AreEqual("20%", panel.PercentText);
        Assert.AreEqual(20d, panel.ProgressValue, 0.001);
    }

    [TestMethod]
    public void CodexApiCostPanel_ShowRemaining_DisplaysBudgetLeft()
    {
        var panel = new CodexApiCostPanelViewModel(Guid.NewGuid());
        panel.Update(BudgetSummary(spentPercent: 20));

        panel.SetShowRemaining(true);

        Assert.AreEqual("80%", panel.PercentText);
        Assert.AreEqual(80d, panel.ProgressValue, 0.001);
    }

    [TestMethod]
    public void CodexApiCostPanel_ShowRemaining_SurvivesLaterCostRefresh()
    {
        var panel = new CodexApiCostPanelViewModel(Guid.NewGuid());
        panel.SetShowRemaining(true);

        panel.Update(BudgetSummary(spentPercent: 20));

        Assert.AreEqual("80%", panel.PercentText);
    }

    [TestMethod]
    public void CodexApiCostPanel_ShowRemaining_OverBudgetBottomsOutAtZero()
    {
        var panel = new CodexApiCostPanelViewModel(Guid.NewGuid());
        panel.Update(BudgetSummary(spentPercent: 140));

        panel.SetShowRemaining(true);

        Assert.AreEqual("0%", panel.PercentText);
        Assert.AreEqual(0d, panel.ProgressValue, 0.001);
    }

    [TestMethod]
    public void CodexApiCostPanel_WithoutBudget_StaysBlankInBothModes()
    {
        var panel = new CodexApiCostPanelViewModel(Guid.NewGuid());
        panel.Update(new CodexApiUsageSummary { EndpointId = Guid.NewGuid(), Name = "E", MonthlyBudget = 0 });

        Assert.AreEqual("", panel.PercentText);
        panel.SetShowRemaining(true);
        Assert.AreEqual("", panel.PercentText);
    }

    private static CodexApiUsageSummary BudgetSummary(double spentPercent) => new()
    {
        EndpointId = Guid.NewGuid(),
        Name = "Endpoint",
        MonthlyBudget = 100,
        MonthlyBudgetPercent = spentPercent,
        TurnCount = 1
    };

    [TestMethod]
    public void UsageStagePercent_ToDisplay_ConvertsUsedToRemaining()
    {
        Assert.AreEqual(80d, UsageStagePercent.ToDisplay(usedPercent: 20, showRemaining: true), 0.001);
    }

    [TestMethod]
    public void UsageStagePercent_ToDisplay_KeepsUsedWhenNotRemaining()
    {
        Assert.AreEqual(20d, UsageStagePercent.ToDisplay(usedPercent: 20, showRemaining: false), 0.001);
    }

    [TestMethod]
    public void UsageStagePercent_ToUsed_ConvertsRemainingBackToUsed()
    {
        Assert.AreEqual(20d, UsageStagePercent.ToUsed(displayPercent: 80, showRemaining: true), 0.001);
    }

    [TestMethod]
    public void UsageStagePercent_RoundTrips_ThroughDisplayAndBackToUsed()
    {
        const double original = 29d;

        var displayed = UsageStagePercent.ToDisplay(original, showRemaining: true);
        var restored = UsageStagePercent.ToUsed(displayed, showRemaining: true);

        Assert.AreEqual(original, restored, 0.001);
    }

    // --- Settings stage textbox path (format / parse / validation message) ---

    [TestMethod]
    public void StageTextbox_UsedMode_ShowsStoredDefaultsUnchanged()
    {
        var displayed = new[] { 29d, 49d, 69d, 79d, 84d }
            .Select(used => UsageStagePercent.Format(used, showRemaining: false));

        CollectionAssert.AreEqual(
            new[] { "29", "49", "69", "79", "84" },
            displayed.ToArray());
    }

    [TestMethod]
    public void StageTextbox_RemainingMode_ShowsDefaultsCountingDown()
    {
        // This is the exact conversion the user asked for: stage 1 "20 used" reads "80 remaining".
        var displayed = new[] { 29d, 49d, 69d, 79d, 84d }
            .Select(used => UsageStagePercent.Format(used, showRemaining: true));

        CollectionAssert.AreEqual(
            new[] { "71", "51", "31", "21", "16" },
            displayed.ToArray());
    }

    [TestMethod]
    public void StageTextbox_RemainingMode_ParsesTypedValueBackToUsed()
    {
        Assert.IsTrue(UsageStagePercent.TryParse("80", showRemaining: true, out var used));
        Assert.AreEqual(20d, used, 0.001);
    }

    [TestMethod]
    public void StageTextbox_UsedMode_ParsesTypedValueAsUsed()
    {
        Assert.IsTrue(UsageStagePercent.TryParse("20", showRemaining: false, out var used));
        Assert.AreEqual(20d, used, 0.001);
    }

    [TestMethod]
    public void StageTextbox_FormatThenParse_RoundTripsInBothModes()
    {
        foreach (var showRemaining in new[] { false, true })
        {
            foreach (var used in new[] { 0d, 16d, 29d, 50d, 84d, 100d })
            {
                var text = UsageStagePercent.Format(used, showRemaining);
                Assert.IsTrue(UsageStagePercent.TryParse(text, showRemaining, out var restored));
                Assert.AreEqual(used, restored, 0.001, $"used={used} showRemaining={showRemaining}");
            }
        }
    }

    [TestMethod]
    public void StageTextbox_FormatKeepsFractionsAndIgnoresLocale()
    {
        // "0.##" invariant: a decimal stage must not render as "70,5" under a comma locale, or
        // the value would fail to parse back on the next Apply.
        Assert.AreEqual("70.5", UsageStagePercent.Format(70.5, showRemaining: false));
        Assert.AreEqual("29.5", UsageStagePercent.Format(70.5, showRemaining: true));
    }

    [TestMethod]
    public void StageTextbox_RejectsNonNumericInput()
    {
        Assert.IsFalse(UsageStagePercent.TryParse("abc", showRemaining: false, out _));
        Assert.IsFalse(UsageStagePercent.TryParse("", showRemaining: true, out _));
    }

    [TestMethod]
    public void StageValidationMessage_TellsUserWhichDirectionValuesMustGo()
    {
        StringAssert.Contains(UsageStagePercent.ValidationMessage(showRemaining: false), "increasing");
        StringAssert.Contains(UsageStagePercent.ValidationMessage(showRemaining: true), "decreasing");
    }

    [TestMethod]
    public void Stage5Row_UsedMode_ReadsAsEverythingAboveStage4()
    {
        Assert.AreEqual("above 79%", UsageStagePercent.OpenEndedStageText(79, showRemaining: false));
    }

    [TestMethod]
    public void Stage5Row_RemainingMode_ReadsAsEverythingBelowStage4()
    {
        // used > 79 is the same bucket as remaining < 21. The old textbox rendered this row as
        // "at least 16%" - a floor, when stage 5 is really a ceiling in remaining terms.
        Assert.AreEqual("below 21%", UsageStagePercent.OpenEndedStageText(79, showRemaining: true));
    }

    [TestMethod]
    public void Stage5Row_TracksStage4RatherThanAStoredValue()
    {
        Assert.AreEqual("above 60%", UsageStagePercent.OpenEndedStageText(60, showRemaining: false));
        Assert.AreEqual("below 40%", UsageStagePercent.OpenEndedStageText(60, showRemaining: true));
    }
}
