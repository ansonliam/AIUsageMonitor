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

        // A group with a single active bucket collapses to just the group name, since the
        // bucket's own name (e.g. "Session 5h") adds no disambiguating information.
        Assert.HasCount(1, windows);
        Assert.AreEqual("Gemini Pro", windows[0].Label);
        Assert.AreEqual(73d, windows[0].RemainingPercent!.Value, 0.001);
        Assert.AreEqual(
            new DateTimeOffset(2026, 8, 22, 1, 2, 3, TimeSpan.Zero),
            windows[0].ResetAt);
    }

    [TestMethod]
    public void Parse_RealAntigravityGroupShape_UsesGroupNameAsLabel()
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
        Assert.AreEqual("Gemini Models", windows[0].Label);
        Assert.AreEqual("Claude and GPT models", windows[1].Label);
    }

    [TestMethod]
    public void Parse_MultipleBucketsInGroup_KeepsBucketNameForDisambiguation()
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
        Assert.AreEqual("Gemini Models · Session 5h", windows[0].Label);
        Assert.AreEqual("Gemini Models · Weekly", windows[1].Label);
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
