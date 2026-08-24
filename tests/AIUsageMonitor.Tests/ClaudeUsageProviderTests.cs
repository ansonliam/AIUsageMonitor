using System.Text.Json;
using AIUsageMonitor.Providers;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class ClaudeUsageProviderTests
{
    [TestMethod]
    public void ReadWindow_LowUtilization_IsReadAsPercentNotFraction()
    {
        var window = ReadWindow("""
            {
              "five_hour": { "utilization": 1, "resets_at": "2026-08-24T05:49:59.894761Z" }
            }
            """);

        Assert.IsNotNull(window);
        Assert.AreEqual(99d, window.RemainingPercent, 0.001);
        Assert.AreEqual(
            DateTimeOffset.Parse("2026-08-24T05:49:59.894761Z"),
            window.ResetAt!.Value);
    }

    [TestMethod]
    public void ReadWindow_FractionalUtilization_StaysBelowOnePercentUsed()
    {
        var window = ReadWindow("""{ "five_hour": { "utilization": 0.5 } }""");

        Assert.IsNotNull(window);
        Assert.AreEqual(99.5d, window.RemainingPercent, 0.001);
        Assert.IsNull(window.ResetAt);
    }

    [TestMethod]
    public void ReadWindow_ZeroUtilization_LeavesFullWindowRemaining()
    {
        var window = ReadWindow("""{ "five_hour": { "utilization": 0 } }""");

        Assert.IsNotNull(window);
        Assert.AreEqual(100d, window.RemainingPercent, 0.001);
    }

    [TestMethod]
    public void ReadWindow_ExhaustedWindow_ClampsToZeroRemaining()
    {
        var window = ReadWindow("""{ "seven_day": { "utilization": 100 } }""", "seven_day");

        Assert.IsNotNull(window);
        Assert.AreEqual(0d, window.RemainingPercent, 0.001);
    }

    [TestMethod]
    public void ReadWindow_MissingWindowOrUtilization_ReturnsNull()
    {
        Assert.IsNull(ReadWindow("""{ "seven_day": { "utilization": 20 } }"""));
        Assert.IsNull(ReadWindow("""{ "five_hour": { "resets_at": "2026-08-24T05:49:59Z" } }"""));
        Assert.IsNull(ReadWindow("""{ "five_hour": "unavailable" }"""));
    }

    private static ClaudeUsageProvider.UsageWindow? ReadWindow(
        string json,
        string propertyName = "five_hour")
    {
        using var document = JsonDocument.Parse(json);
        return ClaudeUsageProvider.ReadWindow(document.RootElement, propertyName);
    }
}
