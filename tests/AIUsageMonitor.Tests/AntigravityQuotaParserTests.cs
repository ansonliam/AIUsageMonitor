using System.Text.Json;
using AIUsageMonitor.Providers;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class AntigravityQuotaParserTests
{
    [TestMethod]
    public void Parse_CurrentGroupedResponse_ConvertsRemainingFractionAndSkipsDisabledBuckets()
    {
        using var document = JsonDocument.Parse("""
            {
              "response": {
                "groups": [
                  {
                    "displayName": "Gemini Pro",
                    "description": "Model quota",
                    "buckets": [
                      {
                        "bucketId": "five-hour",
                        "displayName": "Session",
                        "window": "5h",
                        "remainingFraction": 0.73,
                        "resetTime": "2026-08-22T01:02:03Z"
                      },
                      {
                        "bucketId": "disabled",
                        "displayName": "Disabled quota",
                        "remainingFraction": 0.5,
                        "disabled": true
                      }
                    ]
                  }
                ]
              }
            }
            """);

        var windows = AntigravityQuotaParser.Parse(document.RootElement);

        Assert.HasCount(1, windows);
        Assert.AreEqual("Gemini Pro · 5H", windows[0].Label);
        Assert.AreEqual("Gemini Pro", windows[0].GroupName);
        Assert.AreEqual("5H", windows[0].WindowLabel);
        Assert.AreEqual(73d, windows[0].RemainingPercent!.Value, 0.001);
        Assert.AreEqual(
            new DateTimeOffset(2026, 8, 22, 1, 2, 3, TimeSpan.Zero),
            windows[0].ResetAt);
    }

    [TestMethod]
    public void Parse_RealWeeklyOnlyResponse_PreservesGroupAndPeriodWithoutInventingFiveHourUsage()
    {
        using var document = JsonDocument.Parse("""
            {
              "response": {
                "groups": [
                  {
                    "displayName": "Gemini Models",
                    "description": "Models within this group: Gemini Flash, Gemini Pro",
                    "buckets": [
                      {
                        "bucketId": "gemini-weekly",
                        "displayName": "Weekly Limit Remaining",
                        "window": "weekly",
                        "remainingFraction": 0.995192,
                        "resetTime": "2026-08-28T14:27:08Z"
                      }
                    ]
                  },
                  {
                    "displayName": "Claude and GPT models",
                    "description": "Models within this group: Claude Opus, Claude Sonnet, GPT-OSS",
                    "buckets": [
                      {
                        "bucketId": "3p-weekly",
                        "displayName": "Weekly Limit Remaining",
                        "window": "weekly",
                        "remainingFraction": 1,
                        "resetTime": "2026-08-28T14:32:12Z"
                      }
                    ]
                  }
                ]
              }
            }
            """);

        var windows = AntigravityQuotaParser.Parse(document.RootElement);

        Assert.HasCount(2, windows);
        Assert.AreEqual("Gemini Models · W", windows[0].Label);
        Assert.AreEqual("Claude and GPT models · W", windows[1].Label);
        Assert.IsTrue(windows.All(window => window.WindowLabel == "W"));
    }

    [TestMethod]
    public void Parse_MultipleBucketsInGroup_DistinguishesPeriods()
    {
        using var document = JsonDocument.Parse("""
            {
              "response": {
                "groups": [
                  {
                    "displayName": "Gemini Models",
                    "buckets": [
                      { "bucketId": "5h", "displayName": "Session", "window": "5h", "remainingFraction": 0.6 },
                      { "bucketId": "weekly", "displayName": "Weekly", "window": "weekly", "remainingFraction": 0.9 }
                    ]
                  }
                ]
              }
            }
            """);

        var windows = AntigravityQuotaParser.Parse(document.RootElement);

        Assert.HasCount(2, windows);
        Assert.AreEqual("Gemini Models · 5H", windows[0].Label);
        Assert.AreEqual("Gemini Models · W", windows[1].Label);
    }

    [TestMethod]
    public void Parse_FourBucketsInWeeklyFirstOrder_PreservesIndependentValuesAndResets()
    {
        using var document = JsonDocument.Parse("""
            { "response": { "groups": [
              { "displayName": "Gemini Models", "buckets": [
                { "bucketId": "gemini-weekly", "window": "weekly", "remainingFraction": 0.91,
                  "resetTime": "2026-09-10T05:37:31Z" },
                { "bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.57,
                  "resetTime": "2026-09-03T09:00:00Z" }
              ] },
              { "displayName": "Claude and GPT models", "buckets": [
                { "bucketId": "3p-weekly", "remainingFraction": 1,
                  "resetTime": "2026-09-10T05:40:50Z" },
                { "bucketId": "3p-5h", "remainingFraction": 0.25,
                  "resetTime": "2026-09-03T10:00:00Z" }
              ] }
            ] } }
            """);

        var windows = AntigravityQuotaParser.Parse(document.RootElement);

        CollectionAssert.AreEqual(new[] { "5H", "W", "5H", "W" },
            windows.Select(window => window.WindowLabel).ToArray());
        CollectionAssert.AreEqual(new[] { 57d, 91d, 25d, 100d },
            windows.Select(window => Math.Round(window.RemainingPercent!.Value)).ToArray());
        CollectionAssert.AreEqual(new[] { "2026-09-03T09:00:00Z", "2026-09-10T05:37:31Z",
                "2026-09-03T10:00:00Z", "2026-09-10T05:40:50Z" },
            windows.Select(window => window.ResetAt!.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")).ToArray());
    }

    [TestMethod]
    public void Parse_DeprecatedBucketsAndAlternatePercentNames_RemainsCompatible()
    {
        using var document = JsonDocument.Parse("""
            {
              "response": {
                "buckets": [
                  { "display_name": "Weekly", "remaining_percentage": 25 },
                  { "displayName": "Session", "utilization": 0.8 },
                  { "displayName": "Amount only", "remainingAmount": 42 }
                ]
              }
            }
            """);

        var windows = AntigravityQuotaParser.Parse(document.RootElement);

        Assert.HasCount(2, windows);
        Assert.AreEqual(25d, windows[0].RemainingPercent!.Value, 0.001);
        Assert.AreEqual(20d, windows[1].RemainingPercent!.Value, 0.001);
    }
}
