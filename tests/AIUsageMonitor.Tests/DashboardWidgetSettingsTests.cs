using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class DashboardWidgetSettingsTests
{
    private string _temporaryDirectory = null!;
    private string _settingsPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"AIUsageMonitor.Tests-{Guid.NewGuid():N}");
        _settingsPath = Path.Combine(_temporaryDirectory, "window-placement.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void ChangedSettingsPersistWithoutAWindow()
    {
        var settings = new DashboardWidgetSettings(_settingsPath);
        var changeCount = 0;
        settings.Changed += () => changeCount++;

        settings.SetDashboardWidgetVisible(false);
        settings.SetWidgetFont("Silkscreen");
        settings.SetProviderVisibility(Models.ProviderKind.Cursor, false);
        settings.SetDashboardWidgetHeight(64);

        var reloaded = new DashboardWidgetSettings(_settingsPath);

        Assert.IsFalse(reloaded.ShowDashboardWidget);
        Assert.AreEqual("Silkscreen", reloaded.WidgetFont);
        Assert.IsFalse(reloaded.ShowCursor);
        Assert.AreEqual(64, reloaded.DashboardWidgetHeight);
        Assert.IsFalse(reloaded.HasSavedPlacement);
        Assert.AreEqual(4, changeCount);
    }

    [TestMethod]
    public void ReapplyingSameValueDoesNotRaiseChanged()
    {
        var settings = new DashboardWidgetSettings(_settingsPath);
        var changeCount = 0;
        settings.Changed += () => changeCount++;

        settings.SetAlwaysOnTop(settings.AlwaysOnTop);

        Assert.AreEqual(0, changeCount);
    }

    [TestMethod]
    public void WindowPlacementPersistsSeparatelyFromSettingNotifications()
    {
        var settings = new DashboardWidgetSettings(_settingsPath);
        var changeCount = 0;
        settings.Changed += () => changeCount++;

        settings.UpdateWindowPlacement(120, 240, 640, 360);

        var reloaded = new DashboardWidgetSettings(_settingsPath);
        Assert.IsTrue(reloaded.HasSavedPlacement);
        Assert.AreEqual(120, reloaded.Left);
        Assert.AreEqual(240, reloaded.Top);
        Assert.AreEqual(640, reloaded.Width);
        Assert.AreEqual(360, reloaded.Height);
        Assert.AreEqual(0, changeCount);
    }

    [TestMethod]
    public void InvalidUsageColorsDoNotReplacePersistedState()
    {
        var settings = new DashboardWidgetSettings(_settingsPath);

        var succeeded = settings.TrySetUsageColors(
            "not-a-colour",
            settings.LimeColorHex,
            settings.YellowColorHex,
            settings.OrangeColorHex,
            settings.RedColorHex,
            29,
            49,
            69,
            79,
            84);

        Assert.IsFalse(succeeded);
        Assert.AreEqual("#2ECC71", settings.GreenColorHex);
        Assert.IsFalse(File.Exists(_settingsPath));
    }
}
