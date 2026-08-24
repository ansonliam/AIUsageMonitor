using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using AIUsageMonitor.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class CodexApiCostTests
{
    [TestMethod]
    public void EndpointSettingsViewModel_ParsesDayFirstTrackFrom_ForDaysAboveTwelve()
    {
        var vm = new CodexApiEndpointSettingsViewModel(
            new CodexApiEndpointSettings { Name = "Test", Endpoint = "example.com", TrackFrom = DateTimeOffset.Now },
            _ => { });
        vm.TrackFromText = "21/08/2026 11:16";

        var parsed = vm.TryToSettings(out var settings, out var validationError);

        Assert.IsTrue(parsed, validationError);
        Assert.AreEqual(21, settings.TrackFrom.Day);
        Assert.AreEqual(8, settings.TrackFrom.Month);
        Assert.AreEqual(2026, settings.TrackFrom.Year);
        Assert.AreEqual(11, settings.TrackFrom.Hour);
        Assert.AreEqual(16, settings.TrackFrom.Minute);
    }

    [TestMethod]
    public void EndpointSettingsViewModel_RejectsMonthFirstTrackFrom()
    {
        var vm = new CodexApiEndpointSettingsViewModel(
            new CodexApiEndpointSettings { Name = "Test", Endpoint = "example.com", TrackFrom = DateTimeOffset.Now },
            _ => { });
        vm.TrackFromText = "13/25/2026 11:16"; // no valid month-first reading; only day-first works

        var parsedInvalid = vm.TryToSettings(out _, out var validationErrorInvalid);
        Assert.IsFalse(parsedInvalid);
        Assert.IsNotNull(validationErrorInvalid);
    }

    [TestMethod]
    public void EndpointSettingsViewModel_RoundTripsAzureCacheMatchRate()
    {
        var vm = new CodexApiEndpointSettingsViewModel(
            new CodexApiEndpointSettings { Name = "Test", Endpoint = "example.com", TrackFrom = DateTimeOffset.Now },
            _ => { });
        vm.CacheHitRateText = "5.22";

        Assert.IsTrue(vm.TryToSettings(out var settings, out var validationError), validationError);
        Assert.AreEqual(5.22m, settings.CacheHitRatePercent);

        var reloaded = new CodexApiEndpointSettingsViewModel(settings, _ => { });
        Assert.AreEqual("5.22", reloaded.CacheHitRateText);
    }

    [TestMethod]
    public void EndpointSettingsViewModel_RoundTripsManualCostAdjustment()
    {
        var vm = new CodexApiEndpointSettingsViewModel(
            new CodexApiEndpointSettings { Name = "Test", Endpoint = "example.com", TrackFrom = DateTimeOffset.Now },
            _ => { });
        vm.ManualCostAdjustmentText = "11.52";

        Assert.IsTrue(vm.TryToSettings(out var settings, out var validationError), validationError);
        Assert.AreEqual(11.52m, settings.ManualCostAdjustment);

        var reloaded = new CodexApiEndpointSettingsViewModel(settings, _ => { });
        Assert.AreEqual("11.52", reloaded.ManualCostAdjustmentText);
    }

    private readonly List<string> _tempDirectories = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var directory in _tempDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "AIUsageMonitorTests_" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }

    // ---- CodexRuntimeLogScanner ----

    private static void SeedRuntimeLog(string databasePath, IEnumerable<(long Id, long Ts, string Target, string Body)> rows)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE logs (id INTEGER PRIMARY KEY, ts INTEGER, ts_nanos INTEGER, level TEXT, " +
                "target TEXT, feedback_log_body TEXT, module_path TEXT, file TEXT, line INTEGER, " +
                "thread_id TEXT, process_uuid TEXT, estimated_bytes INTEGER)";
            create.ExecuteNonQuery();
        }

        foreach (var row in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO logs (id, ts, target, feedback_log_body) VALUES (@id, @ts, @target, @body)";
            insert.Parameters.AddWithValue("@id", row.Id);
            insert.Parameters.AddWithValue("@ts", row.Ts);
            insert.Parameters.AddWithValue("@target", row.Target);
            insert.Parameters.AddWithValue("@body", row.Body);
            insert.ExecuteNonQuery();
        }
    }

    private const string TurnRequestBody =
        "session_loop{thread_id=01a025c8-1fae-7563-87e2-ad8b2d4e33f4}:turn{turn.id=01a025d2-695f-77c1-b7b3-7f14ddec4a43 model=gpt-5.6-terra}:" +
        "run_sampling_request{turn_id=01a025d2-695f-77c1-b7b3-7f14ddec4a43 model=gpt-5.6-terra}: Request completed method=POST " +
        "url=https://example.openai.azure.com/openai/v1/responses status=200 OK";

    private const string NoTurnEvidenceBody =
        "list_models{refresh_strategy=online}: Request completed method=GET " +
        "url=https://example.openai.azure.com/openai/v1/models?client_version=0.149.0 status=200 OK";

    [TestMethod]
    public void RuntimeLogScanner_ParsesTurnIdModelAndHost_FromRealTracingSpanFormat()
    {
        var root = CreateTempDirectory();
        SeedRuntimeLog(Path.Combine(root, "logs_2.sqlite"), [(1, 1700000000, "codex_http_client::client", TurnRequestBody)]);

        var scanner = new CodexRuntimeLogScanner(root);
        var (requests, checkpoint, _) = scanner.ScanNew(0);

        Assert.AreEqual(1, requests.Count);
        Assert.AreEqual("01a025d2-695f-77c1-b7b3-7f14ddec4a43", requests[0].TurnId);
        Assert.AreEqual("gpt-5.6-terra", requests[0].Model);
        Assert.AreEqual("example.openai.azure.com", requests[0].Url!.Host);
        Assert.AreEqual(1, checkpoint);
    }

    [TestMethod]
    public void RuntimeLogScanner_IgnoresRequestsWithoutTurnEvidence()
    {
        var root = CreateTempDirectory();
        SeedRuntimeLog(Path.Combine(root, "logs_2.sqlite"), [(1, 1700000000, "codex_http_client::client", NoTurnEvidenceBody)]);

        var scanner = new CodexRuntimeLogScanner(root);
        var (requests, _, _) = scanner.ScanNew(0);

        Assert.AreEqual(0, requests.Count);
    }

    [TestMethod]
    public void RuntimeLogScanner_OnlyReturnsRowsAfterCheckpoint()
    {
        var root = CreateTempDirectory();
        SeedRuntimeLog(Path.Combine(root, "logs_2.sqlite"),
        [
            (1, 1700000000, "codex_http_client::client", TurnRequestBody),
            (2, 1700000100, "codex_http_client::client", TurnRequestBody)
        ]);

        var scanner = new CodexRuntimeLogScanner(root);
        var (requests, checkpoint, _) = scanner.ScanNew(1);

        Assert.AreEqual(1, requests.Count);
        Assert.AreEqual(2, checkpoint);
    }

    [TestMethod]
    public void RuntimeLogScanner_MissingDatabase_ReturnsEmptyWithoutThrowing()
    {
        var root = CreateTempDirectory();
        var scanner = new CodexRuntimeLogScanner(root);

        var (requests, checkpoint, _) = scanner.ScanNew(0);

        Assert.AreEqual(0, requests.Count);
        Assert.AreEqual(0, checkpoint);
    }

    // ---- CodexSessionLogScanner ----

    private static string WriteSessionFile(string root, string fileName, string content)
    {
        var path = Path.Combine(root, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [TestMethod]
    public void SessionLogScanner_SumsLastTokenUsage_NeverTotalTokenUsage()
    {
        var root = CreateTempDirectory();
        WriteSessionFile(root, "session.jsonl", string.Join('\n',
        [
            """{"timestamp":"2026-08-21T19:24:00.000Z","type":"turn_context","payload":{"turn_id":"turn-1","model":"gpt-5.6-terra"}}""",
            """{"timestamp":"2026-08-21T19:24:05.000Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":19645,"cached_input_tokens":0,"cache_write_input_tokens":19642,"output_tokens":264,"reasoning_output_tokens":47},"last_token_usage":{"input_tokens":19645,"cached_input_tokens":0,"cache_write_input_tokens":19642,"output_tokens":264,"reasoning_output_tokens":47}}}}""",
            """{"timestamp":"2026-08-21T19:24:10.000Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":45004,"cached_input_tokens":19642,"cache_write_input_tokens":25356,"output_tokens":438,"reasoning_output_tokens":103},"last_token_usage":{"input_tokens":25359,"cached_input_tokens":19642,"cache_write_input_tokens":5714,"output_tokens":174,"reasoning_output_tokens":56}}}}""",
            ""
        ]));

        var scanner = new CodexSessionLogScanner(root);
        var fileStates = new Dictionary<string, CodexSessionFileState>();
        var events = scanner.ScanNew(fileStates);

        Assert.AreEqual(2, events.Count);
        Assert.AreEqual(19645, events[0].InputTokens);
        Assert.AreEqual(25359, events[1].InputTokens);
        Assert.IsTrue(events.All(e => e.TurnId == "turn-1"));
        Assert.IsTrue(events.All(e => e.Model == "gpt-5.6-terra"));

        // total_token_usage's cumulative figures must never be the source of a usage event.
        Assert.IsFalse(events.Any(e => e.InputTokens == 45004));
    }

    [TestMethod]
    public void SessionLogScanner_UsesCumulativeDeltas_AndSkipsRepeatedSnapshots()
    {
        var root = CreateTempDirectory();
        WriteSessionFile(root, "session.jsonl", string.Join('\n',
        [
            """{"timestamp":"2026-08-21T19:00:00.000Z","type":"turn_context","payload":{"turn_id":"turn-1","model":"gpt-5.6-terra"}}""",
            """{"timestamp":"2026-08-21T19:00:01.000Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":80,"cache_write_input_tokens":5,"output_tokens":10},"last_token_usage":{"input_tokens":100,"cached_input_tokens":80,"cache_write_input_tokens":5,"output_tokens":10}}}}""",
            """{"timestamp":"2026-08-21T19:00:02.000Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":80,"cache_write_input_tokens":5,"output_tokens":10},"last_token_usage":{"input_tokens":100,"cached_input_tokens":80,"cache_write_input_tokens":5,"output_tokens":10}}}}""",
            """{"timestamp":"2026-08-21T19:00:03.000Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":140,"cached_input_tokens":100,"cache_write_input_tokens":8,"output_tokens":16},"last_token_usage":{"input_tokens":40,"cached_input_tokens":20,"cache_write_input_tokens":3,"output_tokens":6}}}}""",
            ""
        ]));

        var events = new CodexSessionLogScanner(root).ScanNew(new Dictionary<string, CodexSessionFileState>());

        Assert.AreEqual(2, events.Count);
        Assert.AreEqual(100, events[0].InputTokens);
        Assert.AreEqual(40, events[1].InputTokens);
        Assert.AreEqual(20, events[1].CachedInputTokens);
        Assert.AreEqual(3, events[1].CacheWriteInputTokens);
    }

    [TestMethod]
    public void SessionLogScanner_AttributesEventsToCurrentTurnFromTurnContext()
    {
        var root = CreateTempDirectory();
        WriteSessionFile(root, "session.jsonl", string.Join('\n',
        [
            """{"timestamp":"2026-08-21T19:00:00.000Z","type":"turn_context","payload":{"turn_id":"turn-a","model":"gpt-5.6-sol"}}""",
            """{"timestamp":"2026-08-21T19:00:05.000Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":10,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":5,"reasoning_output_tokens":0}}}}""",
            """{"timestamp":"2026-08-21T19:01:00.000Z","type":"turn_context","payload":{"turn_id":"turn-b","model":"gpt-5.6-luna"}}""",
            """{"timestamp":"2026-08-21T19:01:05.000Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":20,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":8,"reasoning_output_tokens":0}}}}""",
            ""
        ]));

        var scanner = new CodexSessionLogScanner(root);
        var events = scanner.ScanNew(new Dictionary<string, CodexSessionFileState>());

        Assert.AreEqual(2, events.Count);
        Assert.AreEqual("turn-a", events[0].TurnId);
        Assert.AreEqual("gpt-5.6-sol", events[0].Model);
        Assert.AreEqual("turn-b", events[1].TurnId);
        Assert.AreEqual("gpt-5.6-luna", events[1].Model);
    }

    [TestMethod]
    public void SessionLogScanner_SkipsMalformedLine_WithoutThrowing()
    {
        var root = CreateTempDirectory();
        WriteSessionFile(root, "session.jsonl", string.Join('\n',
        [
            """{"timestamp":"2026-08-21T19:00:00.000Z","type":"turn_context","payload":{"turn_id":"turn-a","model":"gpt-5.6-sol"}}""",
            "{ not valid json",
            """{"timestamp":"2026-08-21T19:00:05.000Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":10,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":5,"reasoning_output_tokens":0}}}}""",
            ""
        ]));

        var scanner = new CodexSessionLogScanner(root);
        var events = scanner.ScanNew(new Dictionary<string, CodexSessionFileState>());

        Assert.AreEqual(1, events.Count);
    }

    [TestMethod]
    public void SessionLogScanner_HoldsBackPartialTrailingLine_UntilComplete()
    {
        var root = CreateTempDirectory();
        var path = WriteSessionFile(
            root,
            "session.jsonl",
            """{"timestamp":"2026-08-21T19:00:00.000Z","type":"turn_context","payload":{"turn_id":"turn-a","model":"gpt-5.6-sol"}}""" + "\n" +
            """{"timestamp":"2026-08-21T19:00:05.000Z","type":"event_msg","payload":{"type":"token_count","info":{"last""");

        var scanner = new CodexSessionLogScanner(root);
        var fileStates = new Dictionary<string, CodexSessionFileState>();
        var firstPass = scanner.ScanNew(fileStates);
        Assert.AreEqual(0, firstPass.Count);

        File.AppendAllText(path, "_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":0,\"cache_write_input_tokens\":0,\"output_tokens\":5,\"reasoning_output_tokens\":0}}}}\n");
        var secondPass = scanner.ScanNew(fileStates);
        Assert.AreEqual(1, secondPass.Count);
    }

    // ---- CodexApiCostCalculator / CodexPricingRegistry ----

    [TestMethod]
    public void Calculator_BillsCachedAndCacheWriteSeparately_WithoutDoubleBillingReasoning()
    {
        var usage = new CodexApiUsageEvent
        {
            DedupeKey = "k",
            TurnId = "turn-1",
            Model = "gpt-5.6-terra",
            InputTokens = 1_000_000,
            CachedInputTokens = 200_000,
            CacheWriteInputTokens = 100_000,
            OutputTokens = 50_000,
            ReasoningOutputTokens = 10_000
        };
        var pricing = new EffectiveCodexPricing(
            InputPerMillion: 2m,
            CachedInputPerMillion: 0.5m,
            CacheWritePerMillion: 1m,
            OutputPerMillion: 8m);

        var cost = CodexApiCostCalculator.Calculate(usage, pricing);

        // normal input = 1,000,000 - 200,000 - 100,000 = 700,000
        var expected = 700_000m * 2m / 1_000_000m
            + 200_000m * 0.5m / 1_000_000m
            + 100_000m * 1m / 1_000_000m
            + 50_000m * 8m / 1_000_000m;
        Assert.AreEqual(expected, cost);
    }

    [TestMethod]
    public void Calculator_WorstCase_BillsCachedTokensAtFullInputRate_ButNotCacheWrite()
    {
        var usage = new CodexApiUsageEvent
        {
            DedupeKey = "k",
            TurnId = "turn-1",
            Model = "gpt-5.6-terra",
            InputTokens = 1_000_000,
            CachedInputTokens = 200_000,
            CacheWriteInputTokens = 100_000,
            OutputTokens = 50_000,
            ReasoningOutputTokens = 10_000
        };
        var pricing = new EffectiveCodexPricing(
            InputPerMillion: 2m,
            CachedInputPerMillion: 0.5m,
            CacheWritePerMillion: 1m,
            OutputPerMillion: 8m);

        var worstCase = CodexApiCostCalculator.CalculateWorstCase(usage, pricing);
        var bestCase = CodexApiCostCalculator.Calculate(usage, pricing);

        // billable-at-input-rate = 1,000,000 - 100,000 (cache write) = 900,000 - the "cached" 200,000
        // is NOT discounted here, unlike the best case.
        var expected = 900_000m * 2m / 1_000_000m
            + 100_000m * 1m / 1_000_000m
            + 50_000m * 8m / 1_000_000m;
        Assert.AreEqual(expected, worstCase);
        Assert.IsTrue(worstCase > bestCase, "worst case must never be cheaper than best case");
    }

    [TestMethod]
    public void Calculator_WithCacheHitRate_IgnoresCodexsSelfReportedSplit()
    {
        // Codex claims a huge cache hit (900,000 of 1,000,000 cacheable tokens), but the caller
        // supplies an observed real rate of 5% - the result must be driven entirely by the
        // supplied rate, not by CachedInputTokens.
        var usage = new CodexApiUsageEvent
        {
            DedupeKey = "k",
            TurnId = "turn-1",
            Model = "gpt-5.6-terra",
            InputTokens = 1_000_000,
            CachedInputTokens = 900_000,
            CacheWriteInputTokens = 0,
            OutputTokens = 50_000,
            ReasoningOutputTokens = 0
        };
        var pricing = new EffectiveCodexPricing(
            InputPerMillion: 2m,
            CachedInputPerMillion: 0.5m,
            CacheWritePerMillion: 1m,
            OutputPerMillion: 8m);

        var cost = CodexApiCostCalculator.CalculateWithCacheHitRate(usage, pricing, cacheHitRateFraction: 0.05m);

        var expectedCached = 1_000_000m * 0.05m;
        var expectedNormal = 1_000_000m - expectedCached;
        var expected = expectedNormal * 2m / 1_000_000m + expectedCached * 0.5m / 1_000_000m + 50_000m * 8m / 1_000_000m;
        Assert.AreEqual(expected, cost);
    }

    [TestMethod]
    public void PricingRegistry_UsesAzureAudDefaults_ForKnownModels()
    {
        var registry = new CodexPricingRegistry();
        var endpoint = new CodexApiEndpointSettings();

        foreach (var model in CodexPricingRegistry.KnownModels)
        {
            Assert.IsTrue(registry.TryGetEffectivePricing(endpoint, model, out var pricing), model);
            Assert.IsTrue(pricing.InputPerMillion > 0, model);
            Assert.IsTrue(pricing.OutputPerMillion > 0, model);
        }
    }

    [TestMethod]
    public void PricingRegistry_UsesCompleteEndpointOverride()
    {
        var registry = new CodexPricingRegistry();
        var endpoint = new CodexApiEndpointSettings
        {
            PricingOverrides = new Dictionary<string, ModelPricingOverride>
            {
                ["gpt-5.6-terra"] = new ModelPricingOverride
                {
                    InputPerMillion = 1m,
                    CachedInputPerMillion = 0.5m,
                    CacheWritePerMillion = 0.75m,
                    OutputPerMillion = 4m
                }
            }
        };

        Assert.IsTrue(registry.TryGetEffectivePricing(endpoint, "gpt-5.6-terra", out var pricing));
        Assert.AreEqual(1m, pricing.InputPerMillion);
    }

    [TestMethod]
    public void PricingRegistry_IncompleteOverride_FallsBackToAzureAudDefault()
    {
        var registry = new CodexPricingRegistry();
        var endpoint = new CodexApiEndpointSettings
        {
            PricingOverrides = new Dictionary<string, ModelPricingOverride>
            {
                ["gpt-5.6-terra"] = new ModelPricingOverride { InputPerMillion = 1m }
            }
        };

        Assert.IsTrue(registry.TryGetEffectivePricing(endpoint, "gpt-5.6-terra", out var pricing));
        Assert.AreEqual(2.7894m, pricing.InputPerMillion);
    }

    [TestMethod]
    public void PricingRegistry_UnknownModel_HasNoDefaultAndNoOverride()
    {
        var registry = new CodexPricingRegistry();
        var endpoint = new CodexApiEndpointSettings();

        Assert.IsFalse(registry.TryGetEffectivePricing(endpoint, "gpt-unknown-model", out _));
    }

    // ---- CodexApiCostService end-to-end ----

    private static CodexApiEndpointSettings MakeEndpoint(string name, string endpoint, DateTimeOffset trackFrom, decimal? budget = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Endpoint = endpoint,
        TrackFrom = trackFrom,
        MonthlyBudget = budget,
        PricingOverrides = new Dictionary<string, ModelPricingOverride>
        {
            ["gpt-5.6-terra"] = new ModelPricingOverride
            {
                InputPerMillion = 1m,
                CachedInputPerMillion = 0.5m,
                CacheWritePerMillion = 0.75m,
                OutputPerMillion = 4m
            },
            ["gpt-5.6-luna"] = new ModelPricingOverride
            {
                InputPerMillion = 0.1m,
                CachedInputPerMillion = 0.05m,
                CacheWritePerMillion = 0.08m,
                OutputPerMillion = 0.4m
            }
        }
    };

    private static UsageRefreshService MakeRefreshService() => new(
        [],
        new UsageCacheStore(),
        new AutoRefreshOptions(),
        NullLogger<UsageRefreshService>.Instance);

    private static string TurnContextLine(DateTimeOffset timestamp, string turnId, string model) =>
        "{\"timestamp\":\"" + timestamp.ToString("O") + "\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"" +
        turnId + "\",\"model\":\"" + model + "\"}}";

    private static string TokenCountLine(
        DateTimeOffset timestamp,
        long inputTokens,
        long cachedInputTokens,
        long cacheWriteInputTokens,
        long outputTokens,
        long reasoningOutputTokens) =>
        "{\"timestamp\":\"" + timestamp.ToString("O") + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\"," +
        "\"info\":{\"last_token_usage\":{\"input_tokens\":" + inputTokens +
        ",\"cached_input_tokens\":" + cachedInputTokens +
        ",\"cache_write_input_tokens\":" + cacheWriteInputTokens +
        ",\"output_tokens\":" + outputTokens +
        ",\"reasoning_output_tokens\":" + reasoningOutputTokens + "}}}}";

    private static string RunTurn(string turnId, string model, string host, long id, long unixSeconds) =>
        $"turn{{turn.id={turnId} model={model}}}:run_sampling_request{{turn_id={turnId} model={model}}}: " +
        $"Request completed method=POST url=https://{host}/openai/v1/responses status=200 OK";

    [TestMethod]
    public async Task Service_AttributesUsageOnlyToMatchingEndpoint_AndIgnoresUnattributedTurns()
    {
        var root = CreateTempDirectory();
        var runtimeRoot = Path.Combine(root, "codex");
        var sessionsRoot = Path.Combine(root, "codex", "sessions");
        Directory.CreateDirectory(sessionsRoot);
        Directory.CreateDirectory(runtimeRoot);

        var trackFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;

        SeedRuntimeLog(Path.Combine(runtimeRoot, "logs_2.sqlite"),
        [
            (1, now.ToUnixTimeSeconds(), "codex_http_client::client", RunTurn("turn-attributed", "gpt-5.6-terra", "example.openai.azure.com", 1, now.ToUnixTimeSeconds()))
        ]);

        WriteSessionFile(sessionsRoot, "session.jsonl", string.Join('\n',
        [
            TurnContextLine(now, "turn-attributed", "gpt-5.6-terra"),
            TokenCountLine(now, 1000000, 0, 0, 100000, 0),
            TurnContextLine(now, "turn-unattributed", "gpt-5.6-terra"),
            TokenCountLine(now, 500000, 0, 0, 50000, 0),
            ""
        ]));

        var endpoint = MakeEndpoint("Example Azure Codex", "example.openai.azure.com", trackFrom);
        var settingsStore = new CodexApiCostSettingsStore(Path.Combine(root, "settings.json"));
        settingsStore.Save(new CodexApiCostSettings { Endpoints = [endpoint] });

        var service = new CodexApiCostService(
            new CodexRuntimeLogScanner(runtimeRoot),
            new CodexSessionLogScanner(sessionsRoot),
            new CodexApiCostCache(Path.Combine(root, "cache")),
            settingsStore,
            new CodexPricingRegistry(),
            MakeRefreshService(),
            NullLogger<CodexApiCostService>.Instance);

        await service.RefreshAsync();
        var summaries = service.GetCurrentSummaries();

        Assert.AreEqual(1, summaries.Count);
        var summary = summaries[0];
        Assert.AreEqual(1, summary.TurnCount);
        // 1,000,000 input * $1/M + 100,000 output * $4/M = 1.00 + 0.40 = 1.40
        Assert.AreEqual(1.40m, summary.MonthCost);
    }

    [TestMethod]
    public async Task Service_AppliesManualCostAdjustmentToMonthOnly_ExactlyOnce()
    {
        var root = CreateTempDirectory();
        var runtimeRoot = Path.Combine(root, "codex");
        var sessionsRoot = Path.Combine(root, "codex", "sessions");
        Directory.CreateDirectory(sessionsRoot);
        Directory.CreateDirectory(runtimeRoot);

        var trackFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;

        SeedRuntimeLog(Path.Combine(runtimeRoot, "logs_2.sqlite"),
        [
            (1, now.ToUnixTimeSeconds(), "codex_http_client::client", RunTurn("turn-adjusted", "gpt-5.6-terra", "example.openai.azure.com", 1, now.ToUnixTimeSeconds()))
        ]);

        WriteSessionFile(sessionsRoot, "session.jsonl", string.Join('\n',
        [
            TurnContextLine(now, "turn-adjusted", "gpt-5.6-terra"),
            TokenCountLine(now, 1000000, 0, 0, 100000, 0),
            ""
        ]));

        var endpoint = MakeEndpoint("Example Azure Codex", "example.openai.azure.com", trackFrom);
        endpoint.ManualCostAdjustment = 2.50m;
        var settingsStore = new CodexApiCostSettingsStore(Path.Combine(root, "settings.json"));
        settingsStore.Save(new CodexApiCostSettings { Endpoints = [endpoint] });

        var service = new CodexApiCostService(
            new CodexRuntimeLogScanner(runtimeRoot),
            new CodexSessionLogScanner(sessionsRoot),
            new CodexApiCostCache(Path.Combine(root, "cache")),
            settingsStore,
            new CodexPricingRegistry(),
            MakeRefreshService(),
            NullLogger<CodexApiCostService>.Instance);

        await service.RefreshAsync();
        var summary = service.GetCurrentSummaries()[0];

        // Metered cost is 1.40 (see Service_AttributesUsageOnlyToMatchingEndpoint) and the turn is
        // timestamped now, so it falls inside all three periods. The 2.50 adjustment reconciles the
        // month-to-date invoice, so it must land on Month only - and exactly once, not once per
        // assignment block. Today and 7D stay at the metered figure.
        Assert.AreEqual(3.90m, summary.MonthCost);
        Assert.AreEqual(3.90m, summary.MonthCostHigh);
        Assert.AreEqual(1.40m, summary.SevenDayCost);
        Assert.AreEqual(1.40m, summary.SevenDayCostHigh);
        Assert.AreEqual(1.40m, summary.TodayCost);
        Assert.AreEqual(1.40m, summary.TodayCostHigh);
    }

    [TestMethod]
    public async Task Service_TwoEndpoints_ProduceSeparateSummariesWithoutDoubleCounting()
    {
        var root = CreateTempDirectory();
        var runtimeRoot = Path.Combine(root, "codex");
        var sessionsRoot = Path.Combine(root, "codex", "sessions");
        Directory.CreateDirectory(sessionsRoot);
        Directory.CreateDirectory(runtimeRoot);

        var trackFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;

        SeedRuntimeLog(Path.Combine(runtimeRoot, "logs_2.sqlite"),
        [
            (1, now.ToUnixTimeSeconds(), "codex_http_client::client", RunTurn("turn-a", "gpt-5.6-terra", "example.openai.azure.com", 1, now.ToUnixTimeSeconds())),
            (2, now.ToUnixTimeSeconds(), "codex_http_client::client", RunTurn("turn-b", "gpt-5.6-luna", "second-example.openai.azure.com", 2, now.ToUnixTimeSeconds()))
        ]);

        WriteSessionFile(sessionsRoot, "session.jsonl", string.Join('\n',
        [
            TurnContextLine(now, "turn-a", "gpt-5.6-terra"),
            TokenCountLine(now, 1000000, 0, 0, 0, 0),
            TurnContextLine(now, "turn-b", "gpt-5.6-luna"),
            TokenCountLine(now, 1000000, 0, 0, 0, 0),
            ""
        ]));

        var endpointA = MakeEndpoint("Example Azure Codex", "example.openai.azure.com", trackFrom);
        var endpointB = MakeEndpoint("Second Example Azure Codex", "second-example.openai.azure.com", trackFrom);
        var settingsStore = new CodexApiCostSettingsStore(Path.Combine(root, "settings.json"));
        settingsStore.Save(new CodexApiCostSettings { Endpoints = [endpointA, endpointB] });

        var service = new CodexApiCostService(
            new CodexRuntimeLogScanner(runtimeRoot),
            new CodexSessionLogScanner(sessionsRoot),
            new CodexApiCostCache(Path.Combine(root, "cache")),
            settingsStore,
            new CodexPricingRegistry(),
            MakeRefreshService(),
            NullLogger<CodexApiCostService>.Instance);

        await service.RefreshAsync();
        var summaries = service.GetCurrentSummaries();

        Assert.AreEqual(2, summaries.Count);
        var summaryA = summaries.Single(s => s.EndpointId == endpointA.Id);
        var summaryB = summaries.Single(s => s.EndpointId == endpointB.Id);
        Assert.AreEqual(1, summaryA.TurnCount);
        Assert.AreEqual(1, summaryB.TurnCount);
        // A: terra $1/M input -> 1,000,000 * 1 / 1,000,000 = 1.00 ; B: luna $0.1/M input -> 0.10
        Assert.AreEqual(1.00m, summaryA.MonthCost);
        Assert.AreEqual(0.10m, summaryB.MonthCost);
    }

    [TestMethod]
    public async Task Service_TrackFrom_ExcludesUsageBeforeIt()
    {
        var root = CreateTempDirectory();
        var runtimeRoot = Path.Combine(root, "codex");
        var sessionsRoot = Path.Combine(root, "codex", "sessions");
        Directory.CreateDirectory(sessionsRoot);
        Directory.CreateDirectory(runtimeRoot);

        var now = DateTimeOffset.UtcNow;
        var trackFrom = now.AddMinutes(5); // usage below happens before TrackFrom

        SeedRuntimeLog(Path.Combine(runtimeRoot, "logs_2.sqlite"),
        [
            (1, now.ToUnixTimeSeconds(), "codex_http_client::client", RunTurn("turn-a", "gpt-5.6-terra", "example.openai.azure.com", 1, now.ToUnixTimeSeconds()))
        ]);

        WriteSessionFile(sessionsRoot, "session.jsonl", string.Join('\n',
        [
            TurnContextLine(now, "turn-a", "gpt-5.6-terra"),
            TokenCountLine(now, 1000000, 0, 0, 0, 0),
            ""
        ]));

        var endpoint = MakeEndpoint("Example Azure Codex", "example.openai.azure.com", trackFrom);
        var settingsStore = new CodexApiCostSettingsStore(Path.Combine(root, "settings.json"));
        settingsStore.Save(new CodexApiCostSettings { Endpoints = [endpoint] });

        var service = new CodexApiCostService(
            new CodexRuntimeLogScanner(runtimeRoot),
            new CodexSessionLogScanner(sessionsRoot),
            new CodexApiCostCache(Path.Combine(root, "cache")),
            settingsStore,
            new CodexPricingRegistry(),
            MakeRefreshService(),
            NullLogger<CodexApiCostService>.Instance);

        await service.RefreshAsync();
        var summary = service.GetCurrentSummaries().Single();

        Assert.AreEqual(0, summary.TurnCount);
        Assert.AreEqual(0m, summary.MonthCost);
    }

    [TestMethod]
    public async Task Service_MonthlyBudgetPercent_IsComputedFromMonthCost()
    {
        var root = CreateTempDirectory();
        var runtimeRoot = Path.Combine(root, "codex");
        var sessionsRoot = Path.Combine(root, "codex", "sessions");
        Directory.CreateDirectory(sessionsRoot);
        Directory.CreateDirectory(runtimeRoot);

        var trackFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;

        SeedRuntimeLog(Path.Combine(runtimeRoot, "logs_2.sqlite"),
        [
            (1, now.ToUnixTimeSeconds(), "codex_http_client::client", RunTurn("turn-a", "gpt-5.6-terra", "example.openai.azure.com", 1, now.ToUnixTimeSeconds()))
        ]);

        WriteSessionFile(sessionsRoot, "session.jsonl", string.Join('\n',
        [
            TurnContextLine(now, "turn-a", "gpt-5.6-terra"),
            TokenCountLine(now, 15000000, 0, 0, 0, 0),
            ""
        ]));

        // terra input pricing is $1/M -> 15,000,000 tokens = $15.00 cost against a $30 budget = 50%.
        var endpoint = MakeEndpoint("Example Azure Codex", "example.openai.azure.com", trackFrom, budget: 30m);
        var settingsStore = new CodexApiCostSettingsStore(Path.Combine(root, "settings.json"));
        settingsStore.Save(new CodexApiCostSettings { Endpoints = [endpoint] });

        var service = new CodexApiCostService(
            new CodexRuntimeLogScanner(runtimeRoot),
            new CodexSessionLogScanner(sessionsRoot),
            new CodexApiCostCache(Path.Combine(root, "cache")),
            settingsStore,
            new CodexPricingRegistry(),
            MakeRefreshService(),
            NullLogger<CodexApiCostService>.Instance);

        await service.RefreshAsync();
        var summary = service.GetCurrentSummaries().Single();

        Assert.AreEqual(15.00m, summary.MonthCost);
        Assert.IsNotNull(summary.MonthlyBudgetPercent);
        Assert.AreEqual(50.0, summary.MonthlyBudgetPercent!.Value, 0.001);
    }

    [TestMethod]
    public async Task Service_CacheReload_ReproducesSameAggregateWithoutFullRescan()
    {
        var root = CreateTempDirectory();
        var runtimeRoot = Path.Combine(root, "codex");
        var sessionsRoot = Path.Combine(root, "codex", "sessions");
        Directory.CreateDirectory(sessionsRoot);
        Directory.CreateDirectory(runtimeRoot);

        var trackFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;

        SeedRuntimeLog(Path.Combine(runtimeRoot, "logs_2.sqlite"),
        [
            (1, now.ToUnixTimeSeconds(), "codex_http_client::client", RunTurn("turn-a", "gpt-5.6-terra", "example.openai.azure.com", 1, now.ToUnixTimeSeconds()))
        ]);

        WriteSessionFile(sessionsRoot, "session.jsonl", string.Join('\n',
        [
            TurnContextLine(now, "turn-a", "gpt-5.6-terra"),
            TokenCountLine(now, 1000000, 0, 0, 0, 0),
            ""
        ]));

        var endpoint = MakeEndpoint("Example Azure Codex", "example.openai.azure.com", trackFrom);
        var settingsPath = Path.Combine(root, "settings.json");
        var cachePath = Path.Combine(root, "cache");
        var settingsStore = new CodexApiCostSettingsStore(settingsPath);
        settingsStore.Save(new CodexApiCostSettings { Endpoints = [endpoint] });

        var firstService = new CodexApiCostService(
            new CodexRuntimeLogScanner(runtimeRoot),
            new CodexSessionLogScanner(sessionsRoot),
            new CodexApiCostCache(cachePath),
            settingsStore,
            new CodexPricingRegistry(),
            MakeRefreshService(),
            NullLogger<CodexApiCostService>.Instance);
        await firstService.RefreshAsync();
        var firstSummary = firstService.GetCurrentSummaries().Single();

        // A brand-new service instance reloading the same on-disk cache should reproduce the same
        // aggregate purely from cached state, without needing to rescan the (now-untouched) source files.
        var secondService = new CodexApiCostService(
            new CodexRuntimeLogScanner(runtimeRoot),
            new CodexSessionLogScanner(sessionsRoot),
            new CodexApiCostCache(cachePath),
            settingsStore,
            new CodexPricingRegistry(),
            MakeRefreshService(),
            NullLogger<CodexApiCostService>.Instance);
        await secondService.RefreshAsync();
        var secondSummary = secondService.GetCurrentSummaries().Single();

        Assert.AreEqual(firstSummary.MonthCost, secondSummary.MonthCost);
        Assert.AreEqual(firstSummary.TurnCount, secondSummary.TurnCount);
    }

    // ---- ApiEndpointType backward compatibility ----

    [TestMethod]
    public void SettingsStore_LoadsOldFileWithNoTypeProperty_AsCodexAzureOpenAI()
    {
        var root = CreateTempDirectory();
        var settingsPath = Path.Combine(root, "settings.json");

        // Simulates a settings file written before ApiEndpointType/Type existed - no "Type"
        // property present at all, exactly what pre-existing users have on disk today.
        File.WriteAllText(settingsPath, """
        {
          "Endpoints": [
            {
              "Id": "11111111-1111-1111-1111-111111111111",
              "Name": "Old Codex Endpoint",
              "Endpoint": "example.openai.azure.com",
              "NormalizedHost": "example.openai.azure.com",
              "TrackFrom": "2026-01-01T00:00:00+00:00",
              "PricingOverrides": {},
              "ShowInWidget": true
            }
          ]
        }
        """);

        var store = new CodexApiCostSettingsStore(settingsPath);
        var loaded = store.Load();

        Assert.AreEqual(1, loaded.Endpoints.Count);
        Assert.AreEqual(ApiEndpointType.CodexAzureOpenAI, loaded.Endpoints[0].Type);
        Assert.AreEqual("", loaded.Endpoints[0].AwsRegion);
    }

    // ---- Claude Bedrock cost calculation ----

    [TestMethod]
    public void ClaudeCalculator_BillsEveryTokenCounterAtItsOwnRate_WithNoBestWorstRange()
    {
        var usage = new ClaudeApiUsageEvent
        {
            DedupeKey = "k",
            Model = "claude-sonnet",
            InputTokens = 1_000_000,
            CachedInputTokens = 500_000,
            CacheWriteInputTokens = 250_000,
            OutputTokens = 100_000
        };
        var pricing = new EffectiveClaudePricing(
            InputPerMillion: 3m,
            CachedInputPerMillion: 0.3m,
            CacheWritePerMillion: 3.75m,
            OutputPerMillion: 15m);

        var cost = ClaudeApiCostCalculator.Calculate(usage, pricing);

        // Unlike Codex, Claude/Bedrock's input_tokens is not inclusive of cache tokens - every
        // counter is billed once, at its own rate, with no subtraction needed.
        var expected = 1_000_000m * 3m / 1_000_000m
            + 500_000m * 0.3m / 1_000_000m
            + 250_000m * 3.75m / 1_000_000m
            + 100_000m * 15m / 1_000_000m;
        Assert.AreEqual(expected, cost);
    }

    [TestMethod]
    public void ClaudePricingRegistry_UsesBedrockAudDefaults_ForCurrentModels()
    {
        var registry = new ClaudePricingRegistry();
        var endpoint = new CodexApiEndpointSettings { Type = ApiEndpointType.ClaudeAwsBedrock };

        Assert.IsTrue(registry.TryGetEffectivePricing(endpoint, "claude-opus", out var opus));
        Assert.AreEqual(6.9735m, opus.InputPerMillion);
        Assert.IsTrue(registry.TryGetEffectivePricing(endpoint, "claude-sonnet", out var sonnet));
        Assert.IsTrue(sonnet.InputPerMillion is 2.7894m or 4.1841m);
        Assert.IsTrue(registry.TryGetEffectivePricing(endpoint, "claude-haiku", out var haiku));
        Assert.AreEqual(1.3947m, haiku.InputPerMillion);
        Assert.IsFalse(registry.TryGetEffectivePricing(endpoint, "claude-other", out _));
    }

    [TestMethod]
    public void ClaudePricingRegistry_UsesCompleteEndpointOverride()
    {
        var registry = new ClaudePricingRegistry();
        var endpoint = new CodexApiEndpointSettings
        {
            Type = ApiEndpointType.ClaudeAwsBedrock,
            PricingOverrides = new Dictionary<string, ModelPricingOverride>
            {
                ["claude-sonnet"] = new ModelPricingOverride
                {
                    InputPerMillion = 3m,
                    CachedInputPerMillion = 0.3m,
                    CacheWritePerMillion = 3.75m,
                    OutputPerMillion = 15m
                }
            }
        };

        Assert.IsTrue(registry.TryGetEffectivePricing(endpoint, "claude-sonnet", out var pricing));
        Assert.AreEqual(3m, pricing.InputPerMillion);
    }

    [TestMethod]
    public void ClaudePricingRegistry_OverrideWinsEvenWhenABuiltInDefaultExists()
    {
        // claude-sonnet has a built-in default (2.00/M input) - a complete user override must still
        // take priority over it, exactly like CodexPricingRegistry's override-over-default contract.
        var registry = new ClaudePricingRegistry();
        var endpoint = new CodexApiEndpointSettings
        {
            Type = ApiEndpointType.ClaudeAwsBedrock,
            PricingOverrides = new Dictionary<string, ModelPricingOverride>
            {
                ["claude-sonnet"] = new ModelPricingOverride
                {
                    InputPerMillion = 99m,
                    CachedInputPerMillion = 99m,
                    CacheWritePerMillion = 99m,
                    OutputPerMillion = 99m
                }
            }
        };

        Assert.IsTrue(registry.TryGetEffectivePricing(
            endpoint, "claude-sonnet", "us.anthropic.claude-sonnet-4-5-20250929-v1:0", out var pricing));
        Assert.AreEqual(99m, pricing.InputPerMillion);
    }

    [TestMethod]
    public void ClaudePricingRegistry_UsesAudDefaultRegardlessOfRawModelAlias()
    {
        var registry = new ClaudePricingRegistry();
        var endpoint = new CodexApiEndpointSettings { Type = ApiEndpointType.ClaudeAwsBedrock };

        Assert.IsTrue(registry.TryGetEffectivePricing(
            endpoint, "claude-sonnet", "anthropic.claude-sonnet-5-20260101-v1:0", out var pricing));
        Assert.IsTrue(pricing.InputPerMillion is 2.7894m or 4.1841m);
    }

    [TestMethod]
    public void ClaudePricingRegistry_UsesSameGlobalAudDefaultAcrossAliases()
    {
        var registry = new ClaudePricingRegistry();
        var endpoint = new CodexApiEndpointSettings { Type = ApiEndpointType.ClaudeAwsBedrock };

        foreach (var opusId in new[]
                 {
                     "anthropic.claude-opus-4-5-20250101-v1:0",
                     "anthropic.claude-opus-4-8-20250601-v1:0",
                     "anthropic.claude-opus-5-20260101-v1:0"
                 })
        {
            Assert.IsTrue(registry.TryGetEffectivePricing(endpoint, "claude-opus", opusId, out var pricing), opusId);
            Assert.AreEqual(6.9735m, pricing.InputPerMillion);
        }

        Assert.IsTrue(registry.TryGetEffectivePricing(
            endpoint, "claude-haiku", "anthropic.claude-haiku-4-5-20250101-v1:0", out var haiku));
        Assert.AreEqual(1.3947m, haiku.InputPerMillion);
    }

    [TestMethod]
    public void ClaudePricingRegistry_EndpointPricingIsIndependentOfRawRegion()
    {
        var registry = new ClaudePricingRegistry();
        var endpoint = new CodexApiEndpointSettings { Type = ApiEndpointType.ClaudeAwsBedrock };

        Assert.IsTrue(registry.TryGetEffectivePricing(
            endpoint, "claude-sonnet", "us.anthropic.claude-sonnet-5-20260101-v1:0", out var pricing));
        Assert.IsTrue(pricing.InputPerMillion is 2.7894m or 4.1841m);
    }

    [TestMethod]
    public void ClaudePricingRegistry_UsesSonnetIntroductoryAndStandardDates()
    {
        var registry = new ClaudePricingRegistry();
        var endpoint = new CodexApiEndpointSettings { Type = ApiEndpointType.ClaudeAwsBedrock };

        Assert.IsTrue(registry.TryGetEffectivePricing(
            endpoint, "claude-sonnet", rawModelId: null,
            usageTimestamp: new DateTimeOffset(2026, 8, 31, 23, 59, 59, TimeSpan.Zero), out var introductory));
        Assert.AreEqual(2.7894m, introductory.InputPerMillion);
        Assert.IsTrue(registry.TryGetEffectivePricing(
            endpoint, "claude-sonnet", rawModelId: null,
            usageTimestamp: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), out var standard));
        Assert.AreEqual(4.1841m, standard.InputPerMillion);
    }

    // ---- ClaudeSessionLogScanner ----

    // Explicit inactive config for tests that must stay deterministic regardless of whatever
    // Bedrock/Mantle settings happen to be configured for real on the machine running the suite -
    // the scanner's default provider reads live from ~/.claude/settings.json/environment.
    private static readonly Func<ClaudeBedrockRoutingConfig> InactiveBedrockConfig =
        () => new ClaudeBedrockRoutingConfig(IsActive: false, Region: "");

    private static string ClaudeAssistantLine(
        DateTimeOffset timestamp,
        string messageId,
        string model,
        long inputTokens,
        long cacheReadTokens,
        long cacheCreationTokens,
        long outputTokens) =>
        "{\"timestamp\":\"" + timestamp.ToString("O") + "\",\"type\":\"assistant\",\"message\":{\"id\":\"" +
        messageId + "\",\"model\":\"" + model + "\",\"usage\":{\"input_tokens\":" + inputTokens +
        ",\"cache_read_input_tokens\":" + cacheReadTokens +
        ",\"cache_creation_input_tokens\":" + cacheCreationTokens +
        ",\"output_tokens\":" + outputTokens + "}}}";

    [TestMethod]
    public void ClaudeSessionLogScanner_DetectsBedrockCrossRegionModelId_AndExtractsRegion()
    {
        var root = CreateTempDirectory();
        var now = DateTimeOffset.UtcNow;
        WriteSessionFile(root, "session.jsonl", string.Join('\n',
        [
            ClaudeAssistantLine(now, "msg_1", "us.anthropic.claude-3-5-sonnet-20241022-v2:0", 100, 10, 5, 50),
            ""
        ]));

        var scanner = new ClaudeSessionLogScanner(root, InactiveBedrockConfig);
        var events = scanner.ScanNew(new Dictionary<string, CodexSessionFileState>());

        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("us", events[0].Region);
        Assert.AreEqual("claude-sonnet", events[0].Model);
        Assert.AreEqual("us.anthropic.claude-3-5-sonnet-20241022-v2:0", events[0].RawModelId);
        Assert.AreEqual(100, events[0].InputTokens);
        Assert.AreEqual(10, events[0].CachedInputTokens);
        Assert.AreEqual(5, events[0].CacheWriteInputTokens);
        Assert.AreEqual(50, events[0].OutputTokens);
    }

    [TestMethod]
    public void ClaudeSessionLogScanner_IgnoresFirstPartyApiModelIds_NotJustAnythingClaude()
    {
        var root = CreateTempDirectory();
        var now = DateTimeOffset.UtcNow;
        WriteSessionFile(root, "session.jsonl", string.Join('\n',
        [
            ClaudeAssistantLine(now, "msg_1", "claude-3-5-sonnet-20241022", 100, 0, 0, 50),
            ""
        ]));

        var scanner = new ClaudeSessionLogScanner(root, InactiveBedrockConfig);
        var events = scanner.ScanNew(new Dictionary<string, CodexSessionFileState>());

        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public void ClaudeSessionLogScanner_ConfigBedrockActive_CountsPlainModelIds_WithConfiguredRegion()
    {
        var root = CreateTempDirectory();
        var now = DateTimeOffset.UtcNow;
        WriteSessionFile(root, "session.jsonl", string.Join('\n',
        [
            ClaudeAssistantLine(now, "msg_1", "claude-sonnet-5", 100, 10, 5, 50),
            ""
        ]));

        var scanner = new ClaudeSessionLogScanner(
            root,
            () => new ClaudeBedrockRoutingConfig(IsActive: true, Region: "ap-southeast-2"));
        var events = scanner.ScanNew(new Dictionary<string, CodexSessionFileState>());

        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("ap-southeast-2", events[0].Region);
        Assert.AreEqual("claude-sonnet", events[0].Model);
        Assert.AreEqual("claude-sonnet-5", events[0].RawModelId);
    }

    [TestMethod]
    public void ClaudeSessionLogScanner_ConfigBedrockActive_StillIgnoresSyntheticSentinel()
    {
        var root = CreateTempDirectory();
        var now = DateTimeOffset.UtcNow;
        WriteSessionFile(root, "session.jsonl", string.Join('\n',
        [
            ClaudeAssistantLine(now, "msg_1", "<synthetic>", 100, 0, 0, 50),
            ""
        ]));

        var scanner = new ClaudeSessionLogScanner(
            root,
            () => new ClaudeBedrockRoutingConfig(IsActive: true, Region: "ap-southeast-2"));
        var events = scanner.ScanNew(new Dictionary<string, CodexSessionFileState>());

        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public void ClaudeSessionLogScanner_ConfigBedrockInactive_StillIgnoresPlainModelIds()
    {
        var root = CreateTempDirectory();
        var now = DateTimeOffset.UtcNow;
        WriteSessionFile(root, "session.jsonl", string.Join('\n',
        [
            ClaudeAssistantLine(now, "msg_1", "claude-sonnet-5", 100, 0, 0, 50),
            ""
        ]));

        var scanner = new ClaudeSessionLogScanner(root, InactiveBedrockConfig);
        var events = scanner.ScanNew(new Dictionary<string, CodexSessionFileState>());

        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public void ClaudeSessionLogScanner_RepeatedMessageId_DedupesToOneEventOnceKeyed()
    {
        var root = CreateTempDirectory();
        var now = DateTimeOffset.UtcNow;

        // Streamed responses can emit the same assistant message id across multiple JSONL lines
        // (e.g. partial usage growing as the stream progresses) - the last one wins once keyed by
        // DedupeKey, exactly as CodexApiCostService does for _claudeUsageEvents.
        WriteSessionFile(root, "session.jsonl", string.Join('\n',
        [
            ClaudeAssistantLine(now, "msg_1", "us.anthropic.claude-3-5-sonnet-20241022-v2:0", 50, 0, 0, 10),
            ClaudeAssistantLine(now, "msg_1", "us.anthropic.claude-3-5-sonnet-20241022-v2:0", 100, 10, 5, 50),
            ""
        ]));

        var scanner = new ClaudeSessionLogScanner(root, InactiveBedrockConfig);
        var events = scanner.ScanNew(new Dictionary<string, CodexSessionFileState>());

        Assert.AreEqual(2, events.Count);
        Assert.IsTrue(events.All(e => e.DedupeKey == "msg_1"));

        var keyed = new Dictionary<string, ClaudeApiUsageEvent>();
        foreach (var usageEvent in events)
        {
            keyed[usageEvent.DedupeKey] = usageEvent;
        }

        Assert.AreEqual(1, keyed.Count);
        Assert.AreEqual(100, keyed["msg_1"].InputTokens);
    }

    [TestMethod]
    public void ClaudeSessionLogScanner_FallsBackToRequestId_WhenMessageIdMissing()
    {
        var root = CreateTempDirectory();
        var now = DateTimeOffset.UtcNow;
        var line = "{\"timestamp\":\"" + now.ToString("O") + "\",\"requestId\":\"req_1\",\"type\":\"assistant\"," +
            "\"message\":{\"model\":\"us.anthropic.claude-3-5-sonnet-20241022-v2:0\",\"usage\":{\"input_tokens\":10," +
            "\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0,\"output_tokens\":5}}}";
        WriteSessionFile(root, "session.jsonl", line + "\n");

        var scanner = new ClaudeSessionLogScanner(root, InactiveBedrockConfig);
        var events = scanner.ScanNew(new Dictionary<string, CodexSessionFileState>());

        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("req_1", events[0].DedupeKey);
    }

    [TestMethod]
    public void ClaudeSessionLogScanner_MissingDirectory_ReturnsEmptyWithoutThrowing()
    {
        var root = Path.Combine(CreateTempDirectory(), "does-not-exist");
        var scanner = new ClaudeSessionLogScanner(root, InactiveBedrockConfig);

        var events = scanner.ScanNew(new Dictionary<string, CodexSessionFileState>());

        Assert.AreEqual(0, events.Count);
    }

    // ---- CodexApiCostService end-to-end: missing Claude pricing ----

    [TestMethod]
    public async Task Service_ClaudeBedrockEndpoint_WithNoUserPricing_ReportsPricingRequired()
    {
        var root = CreateTempDirectory();
        var codexRuntimeRoot = Path.Combine(root, "codex");
        var codexSessionsRoot = Path.Combine(root, "codex", "sessions");
        var claudeProjectsRoot = Path.Combine(root, "claude-projects");
        Directory.CreateDirectory(codexRuntimeRoot);
        Directory.CreateDirectory(codexSessionsRoot);
        Directory.CreateDirectory(claudeProjectsRoot);

        var trackFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;

        // Sonnet 5 (not the pricier 4.5/4.6 generation), no regional prefix -> Standard default
        // rate of $2.00/M input, $10.00/M output, with no PricingOverrides entered for it at all.
        WriteSessionFile(claudeProjectsRoot, "session.jsonl", string.Join('\n',
        [
            ClaudeAssistantLine(now, "msg_1", "anthropic.claude-sonnet-5-20260101-v1:0", 1_000_000, 0, 0, 100_000),
            ""
        ]));

        var endpoint = new CodexApiEndpointSettings
        {
            Id = Guid.NewGuid(),
            Type = ApiEndpointType.ClaudeAwsBedrock,
            Name = "B2C Claude Bedrock",
            TrackFrom = trackFrom,
            AwsRegion = "ap-southeast-2"
        };
        var settingsStore = new CodexApiCostSettingsStore(Path.Combine(root, "settings.json"));
        settingsStore.Save(new CodexApiCostSettings { Endpoints = [endpoint] });

        var service = new CodexApiCostService(
            new CodexRuntimeLogScanner(codexRuntimeRoot),
            new CodexSessionLogScanner(codexSessionsRoot),
            new CodexApiCostCache(Path.Combine(root, "cache")),
            settingsStore,
            new CodexPricingRegistry(),
            MakeRefreshService(),
            NullLogger<CodexApiCostService>.Instance,
            new ClaudeSessionLogScanner(claudeProjectsRoot, InactiveBedrockConfig),
            new ClaudePricingRegistry());

        await service.RefreshAsync();
        var summary = service.GetCurrentSummaries().Single();

        var expectedPricing = ClaudePricingRegistry.GetDefault("claude-sonnet", now)!;
        var expected = expectedPricing.InputPerMillion + expectedPricing.OutputPerMillion * 0.1m;
        Assert.AreEqual(expected, summary.MonthCost);
        Assert.IsFalse(summary.PricingUnavailable);
        Assert.AreEqual(1, summary.RequestCount);
    }

    // ---- ClaudeBedrockRoutingConfigReader ----

    [TestMethod]
    public void ClaudeBedrockRoutingConfigReader_ReadsBedrockFlagAndRegion_FromSettingsJson()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "settings.json"), """
            { "env": { "CLAUDE_CODE_USE_BEDROCK": "1", "AWS_REGION": "ap-southeast-2" } }
            """);

        var config = ClaudeBedrockRoutingConfigReader.Read(root, _ => null);

        Assert.IsTrue(config.IsActive);
        Assert.AreEqual("ap-southeast-2", config.Region);
    }

    [TestMethod]
    public void ClaudeBedrockRoutingConfigReader_ReadsMantleFlagAndRegion_FromEnvironmentVariables()
    {
        var root = CreateTempDirectory();
        var env = new Dictionary<string, string?>
        {
            ["CLAUDE_CODE_USE_MANTLE"] = "1",
            ["ANTHROPIC_BEDROCK_MANTLE_BASE_URL"] = "https://bedrock-mantle.ap-southeast-4.api.aws/anthropic"
        };

        var config = ClaudeBedrockRoutingConfigReader.Read(root, key => env.GetValueOrDefault(key));

        Assert.IsTrue(config.IsActive);
        Assert.AreEqual("ap-southeast-4", config.Region);
    }

    [TestMethod]
    public void ClaudeBedrockRoutingConfigReader_MissingSettingsFileAndNoEnv_IsInactiveWithoutThrowing()
    {
        var root = Path.Combine(CreateTempDirectory(), "does-not-exist");

        var config = ClaudeBedrockRoutingConfigReader.Read(root, _ => null);

        Assert.IsFalse(config.IsActive);
        Assert.AreEqual("", config.Region);
    }

    [TestMethod]
    public void ClaudeBedrockRoutingConfigReader_MalformedSettingsJson_IsInactiveWithoutThrowing()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "settings.json"), "{ not valid json");

        var config = ClaudeBedrockRoutingConfigReader.Read(root, _ => null);

        Assert.IsFalse(config.IsActive);
        Assert.AreEqual("", config.Region);
    }
}
