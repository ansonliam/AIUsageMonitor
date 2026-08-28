using System.Diagnostics;
using AIUsageMonitor.Models;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Services;

// Orchestrates the whole Codex API Cost pipeline: scan Codex's own runtime log and session
// JSONL files (read-only, never modifying anything under ~/.codex), join by turn id, and route
// each attributed turn's usage to whichever configured endpoint(s) it belongs to. Piggybacks on
// the existing Codex quota refresh cadence via UsageRefreshService rather than adding a new
// timer or touching UsagePollingService.
public sealed class CodexApiCostService
{
    private readonly CodexRuntimeLogScanner _runtimeLogScanner;
    private readonly CodexSessionLogScanner _sessionLogScanner;
    private readonly CodexApiCostCache _cache;
    private readonly CodexApiCostSettingsStore _settingsStore;
    private readonly CodexPricingRegistry _pricingRegistry;
    private readonly ClaudeSessionLogScanner? _claudeSessionLogScanner;
    private readonly ClaudePricingRegistry? _claudePricingRegistry;
    private readonly ICodexProviderRoutingState? _codexRoutingState;
    private readonly IClaudeThirdPartyRoutingState? _claudeRoutingState;
    private readonly ILogger<CodexApiCostService> _logger;
    private readonly object _syncRoot = new();

    private CodexApiScanState _scanState;
    private Dictionary<string, CodexTurnAttribution> _attributions;
    private Dictionary<string, CodexApiUsageEvent> _usageEvents;
    private Dictionary<string, ClaudeApiUsageEvent> _claudeUsageEvents;
    private Dictionary<Guid, CodexApiUsageSummary> _currentSummaries = [];
    private bool _scanStateLoaded;
    private bool _codexCacheLoaded;
    private bool _claudeCacheLoaded;

    // claudeSessionLogScanner/claudePricingRegistry are optional purely so every pre-existing
    // `new CodexApiCostService(...)` call site (this class's own unit tests included) keeps
    // compiling unchanged. Production wiring (App.xaml.cs) registers both with the DI container,
    // so the app itself always gets the Claude Bedrock pipeline; tests that don't care about it
    // simply omit the trailing args and get null, under which Claude Bedrock endpoints are treated
    // as having no usage data (same as if ~/.claude/projects didn't exist).
    public CodexApiCostService(
        CodexRuntimeLogScanner runtimeLogScanner,
        CodexSessionLogScanner sessionLogScanner,
        CodexApiCostCache cache,
        CodexApiCostSettingsStore settingsStore,
        CodexPricingRegistry pricingRegistry,
        UsageRefreshService refreshService,
        ILogger<CodexApiCostService> logger,
        ClaudeSessionLogScanner? claudeSessionLogScanner = null,
        ClaudePricingRegistry? claudePricingRegistry = null,
        ICodexProviderRoutingState? codexRoutingState = null,
        IClaudeThirdPartyRoutingState? claudeRoutingState = null)
    {
        _runtimeLogScanner = runtimeLogScanner;
        _sessionLogScanner = sessionLogScanner;
        _cache = cache;
        _settingsStore = settingsStore;
        _pricingRegistry = pricingRegistry;
        _claudeSessionLogScanner = claudeSessionLogScanner;
        _claudePricingRegistry = claudePricingRegistry;
        _codexRoutingState = codexRoutingState;
        _claudeRoutingState = claudeRoutingState;
        _logger = logger;
        _scanState = new CodexApiScanState();
        _attributions = [];
        _usageEvents = [];
        _claudeUsageEvents = [];

        refreshService.RefreshCompleted += provider =>
        {
            // Piggyback on both Codex's and Claude's own quota refresh cadence rather than adding
            // a new timer - Claude Bedrock endpoints need a refresh trigger the same way Codex
            // endpoints do.
            if (provider == ProviderKind.Codex || provider == ProviderKind.Claude)
            {
                // A steady-state skip is logged at Debug: this fires on every poll tick, and
                // the monitors already log the transition that caused it at Information.
                if (provider == ProviderKind.Codex && !ShouldTrackCodexApiCost)
                {
                    _logger.LogDebug("Codex API cost scan skipped | Reason=NoPerTokenBilling");
                    return;
                }

                if (provider == ProviderKind.Claude && !ShouldTrackClaudeApiCost)
                {
                    _logger.LogDebug("Claude 3P cost scan skipped | Reason=NotRoutedToThirdParty");
                    return;
                }

                _ = RefreshAsync($"ProviderRefresh:{provider}");
            }
        };

        // Both directions matter. Turning tracking on has to start scanning without waiting for
        // the next poll tick; turning it off has to re-run just as promptly, because that pass is
        // what drops the provider back to its small cached display summary.
        if (_codexRoutingState is not null)
        {
            _codexRoutingState.RoutingChanged += usesApiProvider =>
                _ = RefreshAsync(usesApiProvider ? "CodexRoutingEnabled" : "CodexRoutingPaused");
        }

        if (_claudeRoutingState is not null)
        {
            _claudeRoutingState.RoutingChanged += isThirdPartyRouted =>
                _ = RefreshAsync(isThirdPartyRouted ? "Claude3PRoutingEnabled" : "Claude3PRoutingPaused");
        }
    }

    // Null routing state means "no signal wired up" (every pre-existing test call site), which
    // keeps the original always-scan behaviour rather than silently tracking nothing.
    private bool ShouldTrackCodexApiCost => _codexRoutingState?.IsApiProvider ?? true;
    private bool ShouldTrackClaudeApiCost => _claudeRoutingState?.IsThirdPartyRouted ?? true;

    public event Action? SummariesUpdated;

    public IReadOnlyList<CodexApiUsageSummary> GetCurrentSummaries()
    {
        lock (_syncRoot)
        {
            if (_currentSummaries.Count > 0)
            {
                return [.. _currentSummaries.Values];
            }

            // Nothing computed yet this run - fall back to each endpoint's last persisted summary
            // so the UI has something to show before the first live scan completes.
            var settings = _settingsStore.Load();
            return [.. settings.Endpoints
                .Select(endpoint => _cache.LoadSummary(endpoint.Id))
                .Where(summary => summary is not null)
                .Select(summary => summary!)];
        }
    }

    public Task RefreshAsync(string trigger = "Direct") => Task.Run(() => RefreshCore(trigger));

    private void RefreshCore(string trigger)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var scannedProviders = "None";
        try
        {
            // Logged from inside the lock, once the gates are known: which providers this pass
            // actually scans is the whole point of the line, and it cannot be decided before the
            // settings are read.
            lock (_syncRoot)
            {
                // A provider is worth scanning only when its traffic is billed per token AND
                // the user has an endpoint configured to attribute that spend to. Either way the
                // decision is per provider - the two never gate each other.
                var settings = _settingsStore.Load();
                var trackCodex = ShouldTrackCodexApiCost &&
                    settings.Endpoints.Any(e => e.Type == ApiEndpointType.CodexAzureOpenAI);
                var trackClaude = ShouldTrackClaudeApiCost &&
                    settings.Endpoints.Any(e => e.Type == ApiEndpointType.ClaudeAwsBedrock);
                scannedProviders = DescribeScannedProviders(trackCodex, trackClaude);
                _logger.LogInformation(
                    "API cost scan started | Providers={Providers} | API=Local runtime/session logs | Trigger={Trigger}",
                    scannedProviders,
                    trigger);
                EnsureLoaded(trackCodex, trackClaude);

                if (trackCodex)
                {
                    ScanRuntimeLog();
                    ScanSessionLogs();
                }

                if (trackClaude)
                {
                    ScanClaudeSessionLogs();
                }
                else
                {
                    _logger.LogDebug("Claude 3P cost history skipped | Reason=NotRoutedToThirdParty");
                }

                if (trackCodex)
                {
                    var matchedByTurnId = _usageEvents.Values
                        .Count(usageEvent => usageEvent.TurnId is not null && _attributions.ContainsKey(usageEvent.TurnId));
                    _logger.LogInformation(
                        "[CodexApiCost] {UsageEventCount} JSONL token events in store, {AttributionCount} attributed turns in store, {MatchedByTurnId} token events matched to an attributed turn id",
                        _usageEvents.Count,
                        _attributions.Count,
                        matchedByTurnId);
                }
                else
                {
                    _logger.LogDebug("Codex API cost history skipped | Reason=NoPerTokenBilling");
                }

                if (trackCodex)
                {
                    foreach (var endpoint in settings.Endpoints.Where(e => e.Type == ApiEndpointType.CodexAzureOpenAI))
                    {
                        endpoint.NormalizedHost = CodexEndpointNormalizer.TryNormalizeHost(endpoint.Endpoint, out var host)
                            ? host
                            : "";
                    }
                }

                // Two endpoints resolving to the same host are ambiguous - refuse to attribute
                // usage to either rather than double- or mis-counting it (spec section 29).
                var hostCounts = settings.Endpoints
                    .Where(endpoint => endpoint.Type == ApiEndpointType.CodexAzureOpenAI && endpoint.NormalizedHost.Length > 0)
                    .GroupBy(endpoint => endpoint.NormalizedHost)
                    .ToDictionary(group => group.Key, group => group.Count());

                var claudeEndpointCount = settings.Endpoints.Count(e => e.Type == ApiEndpointType.ClaudeAwsBedrock);
                var claudeRegionCounts = settings.Endpoints
                    .Where(e => e.Type == ApiEndpointType.ClaudeAwsBedrock && e.AwsRegion.Trim().Length > 0)
                    .GroupBy(e => e.AwsRegion.Trim().ToLowerInvariant())
                    .ToDictionary(group => group.Key, group => group.Count());

                var summaries = new Dictionary<Guid, CodexApiUsageSummary>();
                foreach (var endpoint in settings.Endpoints)
                {
                    CodexApiUsageSummary summary;
                    var saveSummary = true;
                    if (endpoint.Type == ApiEndpointType.ClaudeAwsBedrock)
                    {
                        if (trackClaude)
                        {
                            summary = BuildClaudeSummary(endpoint, claudeEndpointCount, claudeRegionCounts);
                        }
                        else
                        {
                            summary = BuildPausedSummary(endpoint);
                            saveSummary = false;
                        }
                    }
                    else if (trackCodex)
                    {
                        var isAmbiguousHost = endpoint.NormalizedHost.Length == 0 ||
                            hostCounts.GetValueOrDefault(endpoint.NormalizedHost) > 1;
                        summary = BuildSummary(endpoint, isAmbiguousHost);
                    }
                    else
                    {
                        summary = BuildPausedSummary(endpoint);
                        saveSummary = false;
                    }

                    summaries[endpoint.Id] = summary;
                    if (saveSummary)
                    {
                        _cache.SaveSummary(summary);
                    }
                }

                _currentSummaries = summaries;

                // Re-check the live signal before writing. A scan that started while a provider was
                // billed per token must not write its multi-megabyte cache if that stopped being
                // true while the scan was still running.
                var persistCodex = trackCodex && ShouldTrackCodexApiCost;
                var persistClaude = trackClaude && ShouldTrackClaudeApiCost;
                PruneOldData(persistCodex, persistClaude);
                PersistScanArtifacts(persistCodex, persistClaude);
            }

            SummariesUpdated?.Invoke();
            _logger.LogInformation(
                "API cost scan completed | Providers={Providers} | API=Local runtime/session logs | Trigger={Trigger} | DurationMs={DurationMs}",
                scannedProviders,
                trigger,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (Exception exception)
        {
            // A Codex API Cost scan failure must never take the rest of the app down with it.
            _logger.LogWarning(exception, "[CodexApiCost] Refresh failed and was skipped");
        }
    }

    private static string DescribeScannedProviders(bool trackCodex, bool trackClaude) =>
        (trackCodex, trackClaude) switch
        {
            (true, true) => "OpenAI Codex,Claude Code",
            (true, false) => "OpenAI Codex",
            (false, true) => "Claude Code",
            _ => "None"
        };

    // The display summary for a provider that is not being scanned this pass. The last saved
    // summary keeps its costs visible without loading or rewriting the multi-megabyte history
    // caches, but it was written whenever the provider was last tracked - so the fields the user
    // can still change while it is paused have to come from the current settings instead, and an
    // endpoint with no saved summary at all still needs an entry or it would simply disappear
    // from the widget.
    private CodexApiUsageSummary BuildPausedSummary(CodexApiEndpointSettings endpoint)
    {
        var saved = _cache.LoadSummary(endpoint.Id) ?? new CodexApiUsageSummary { EndpointId = endpoint.Id };
        return saved with
        {
            Name = endpoint.Name,
            MonthlyBudget = endpoint.MonthlyBudget,
            MonthlyBudgetPercent = endpoint.MonthlyBudget is > 0
                ? (double)(saved.MonthCostHigh / endpoint.MonthlyBudget.Value * 100)
                : null,
            ShowInWidget = endpoint.ShowInWidget
        };
    }

    private void EnsureLoaded(bool loadCodex, bool loadClaude)
    {
        if (!loadCodex && !loadClaude)
        {
            return;
        }

        if (!_scanStateLoaded)
        {
            _scanState = _cache.LoadScanState();

            // Caches written before the per-provider split carry one shared version, and both
            // sides were always rebuilt together back then - so the single recorded version is
            // the truth for Claude too, and adopting it spares those installs a pointless one-off
            // replay of every Claude transcript.
            _scanState.ClaudeUsageCacheSchemaVersion ??= _scanState.UsageCacheSchemaVersion;

            _scanStateLoaded = true;
        }

        if (loadCodex && !_codexCacheLoaded)
        {
            _attributions = _cache.LoadAttributions();
            _usageEvents = _cache.LoadUsageEvents();
            _codexCacheLoaded = true;
        }

        if (loadClaude && !_claudeCacheLoaded)
        {
            _claudeUsageEvents = _cache.LoadClaudeUsageEvents();
            _claudeCacheLoaded = true;
        }

        // Prior caches contain summed JSONL snapshots. The versioned rebuild keeps endpoint
        // settings/prices intact but replays the original logs with cumulative-delta handling.
        //
        // Each provider is rebuilt, and stamped, only when it is actually being tracked. Clearing
        // an untracked provider's file offsets would strand its on-disk history: the offsets would
        // say "replay from scratch" while its cache stayed unloaded and unwritten, so the first
        // scan after it was tracked again would overwrite real history with an empty store.
        if (loadCodex && _scanState.UsageCacheSchemaVersion != CodexApiScanState.CurrentUsageCacheSchemaVersion)
        {
            _scanState.UsageCacheSchemaVersion = CodexApiScanState.CurrentUsageCacheSchemaVersion;
            _scanState.SessionFiles = [];
            _usageEvents = [];
        }

        if (loadClaude && _scanState.ClaudeUsageCacheSchemaVersion != CodexApiScanState.CurrentUsageCacheSchemaVersion)
        {
            _scanState.ClaudeUsageCacheSchemaVersion = CodexApiScanState.CurrentUsageCacheSchemaVersion;
            _scanState.ClaudeSessionFiles = [];
            _claudeUsageEvents = [];
        }
    }

    private void ScanRuntimeLog()
    {
        var (requests, newCheckpoint, rowsScanned) = _runtimeLogScanner.ScanNew(_scanState.LastRuntimeLogId);
        _scanState.LastRuntimeLogId = newCheckpoint;

        foreach (var request in requests)
        {
            if (request.TurnId is null)
            {
                continue;
            }

            var host = CodexEndpointNormalizer.TryGetHost(request.Url);
            if (host is null)
            {
                continue;
            }

            _attributions[request.TurnId] = _attributions.TryGetValue(request.TurnId, out var existing)
                ? existing with
                {
                    FirstSeenAt = existing.FirstSeenAt < request.Timestamp ? existing.FirstSeenAt : request.Timestamp,
                    LastSeenAt = existing.LastSeenAt > request.Timestamp ? existing.LastSeenAt : request.Timestamp
                }
                : new CodexTurnAttribution(host, request.Timestamp, request.Timestamp);
        }

        _logger.LogInformation(
            "[CodexApiCost] SQLite: {RowsScanned} candidate rows scanned since checkpoint, {RequestsWithTurnEvidence} had turn id + url, checkpoint now {NewCheckpoint}",
            rowsScanned,
            requests.Count,
            newCheckpoint);
    }

    private void ScanSessionLogs()
    {
        var newEvents = _sessionLogScanner.ScanNew(_scanState.SessionFiles);
        foreach (var usageEvent in newEvents)
        {
            _usageEvents[usageEvent.DedupeKey] = usageEvent;
        }

        _logger.LogInformation(
            "[CodexApiCost] JSONL: {NewEventCount} new token events found this scan, {TotalEventCount} total in store",
            newEvents.Count,
            _usageEvents.Count);
    }

    private void ScanClaudeSessionLogs()
    {
        if (_claudeSessionLogScanner is null)
        {
            return;
        }

        var newEvents = _claudeSessionLogScanner.ScanNew(_scanState.ClaudeSessionFiles);
        foreach (var usageEvent in newEvents)
        {
            _claudeUsageEvents[usageEvent.DedupeKey] = usageEvent;
        }

        _logger.LogInformation(
            "[CodexApiCost] Claude JSONL: {NewEventCount} new Bedrock token events found this scan, {TotalEventCount} total in store",
            newEvents.Count,
            _claudeUsageEvents.Count);
    }

    private CodexApiUsageSummary BuildSummary(CodexApiEndpointSettings endpoint, bool isAmbiguousHost)
    {
        var now = DateTimeOffset.Now;
        var todayStart = new DateTimeOffset(now.Date, now.Offset);
        var sevenDayStart = todayStart.AddDays(-6);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        var effectiveMonthStart = monthStart > endpoint.TrackFrom ? monthStart : endpoint.TrackFrom;

        decimal todayCost = 0, sevenDayCost = 0, monthCost = 0;
        decimal todayCostHigh = 0, sevenDayCostHigh = 0, monthCostHigh = 0;
        var costByModel = new Dictionary<string, decimal>();
        var turnIds = new HashSet<string>();
        var pricingUnavailable = false;

        var matchingAttributionCount = _attributions.Values.Count(a => a.Host == endpoint.NormalizedHost);
        var matchingAttributionTurnIds = _attributions
            .Where(pair => pair.Value.Host == endpoint.NormalizedHost)
            .Select(pair => pair.Key)
            .ToHashSet();
        var eventsMatchedForHost = 0;

        if (!isAmbiguousHost)
        {
            foreach (var usageEvent in _usageEvents.Values)
            {
                // SQLite's runtime log is the sole authority for a turn's endpoint host and
                // timestamp - JSONL is only ever a source for model and last_token_usage, so all
                // TrackFrom/Today/7D/Month filtering below uses the attribution's FirstSeenAt.
                if (usageEvent.TurnId is null ||
                    !_attributions.TryGetValue(usageEvent.TurnId, out var attribution) ||
                    attribution.Host != endpoint.NormalizedHost)
                {
                    continue;
                }

                eventsMatchedForHost++;

                if (attribution.FirstSeenAt < endpoint.TrackFrom)
                {
                    continue;
                }

                var localTimestamp = attribution.FirstSeenAt.ToLocalTime();
                if (localTimestamp < effectiveMonthStart)
                {
                    continue;
                }

                decimal cost = 0;
                decimal costHigh = 0;
                if (_pricingRegistry.TryGetEffectivePricing(endpoint, usageEvent.Model, out var pricing))
                {
                    if (usageEvent.CachedInputTokens <= 0 && endpoint.CacheHitRatePercent is { } cacheHitRatePercent)
                    {
                        // Use the configured Azure rate only when this record has no local cache
                        // telemetry. When exact cached tokens are present they are more specific
                        // than an endpoint-wide percentage and must not be discarded.
                        cost = CodexApiCostCalculator.CalculateWithCacheHitRate(
                            usageEvent,
                            pricing,
                            Math.Clamp(cacheHitRatePercent / 100m, 0m, 1m));
                    }
                    else
                    {
                        cost = CodexApiCostCalculator.Calculate(usageEvent, pricing);
                    }
                    costHigh = cost;
                }
                else
                {
                    pricingUnavailable = true;
                }

                monthCost += cost;
                monthCostHigh += costHigh;
                if (localTimestamp >= sevenDayStart)
                {
                    sevenDayCost += cost;
                    sevenDayCostHigh += costHigh;
                }

                if (localTimestamp >= todayStart)
                {
                    todayCost += cost;
                    todayCostHigh += costHigh;
                }

                costByModel[usageEvent.Model] = costByModel.GetValueOrDefault(usageEvent.Model) + cost;
                turnIds.Add(usageEvent.TurnId);
            }
        }

        // A manual reconciliation adjustment aligns the month-to-date total with the user's latest
        // provider invoice while subsequent local usage continues to accumulate on top of it.
        // Month-to-date only, deliberately: the gap it corrects accrued over the whole billing
        // period, not in the last 24 hours or 7 days. Adding it to Today would make Today report
        // spend that did not happen today, and would break the Today <= 7D <= Month ordering the
        // widget relies on.
        monthCost += endpoint.ManualCostAdjustment;
        monthCostHigh += endpoint.ManualCostAdjustment;

        // Uses the worst-case (no-cache-credit) figure for budget alerting - safer to warn early
        // than to under-warn because Codex's self-reported cache rate turned out optimistic.
        double? budgetPercent = endpoint.MonthlyBudget is > 0
            ? (double)(monthCostHigh / endpoint.MonthlyBudget.Value * 100)
            : null;

        _logger.LogInformation(
            "[CodexApiCost] Endpoint '{Name}' host={NormalizedHost} ambiguous={IsAmbiguousHost}: " +
            "{MatchingAttributionCount} attributed turns for this host ({MatchingAttributionTurnIds} unique), " +
            "{EventsMatchedForHost} token events matched by turn id for this host (before TrackFrom/date filtering), " +
            "final: {TurnCount} turns counted, PricingUnavailable={PricingUnavailable}, " +
            "Month={MonthCost} (worst {MonthCostHigh}) 7D={SevenDayCost} (worst {SevenDayCostHigh}) " +
            "Today={TodayCost} (worst {TodayCostHigh})",
            endpoint.Name,
            endpoint.NormalizedHost,
            isAmbiguousHost,
            matchingAttributionCount,
            matchingAttributionTurnIds.Count,
            eventsMatchedForHost,
            turnIds.Count,
            pricingUnavailable,
            monthCost,
            monthCostHigh,
            sevenDayCost,
            sevenDayCostHigh,
            todayCost,
            todayCostHigh);

        return new CodexApiUsageSummary
        {
            EndpointId = endpoint.Id,
            Name = endpoint.Name,
            TodayCost = todayCost,
            SevenDayCost = sevenDayCost,
            MonthCost = monthCost,
            TodayCostHigh = todayCostHigh,
            SevenDayCostHigh = sevenDayCostHigh,
            MonthCostHigh = monthCostHigh,
            MonthlyBudget = endpoint.MonthlyBudget,
            MonthlyBudgetPercent = budgetPercent,
            PricingUnavailable = pricingUnavailable,
            ShowInWidget = endpoint.ShowInWidget,
            CacheHitRatePercentUsed = endpoint.CacheHitRatePercent,
            TurnCount = turnIds.Count,
            RequestCount = turnIds.Count,
            CostByModel = costByModel
        };
    }

    // Builds a summary for a Claude Bedrock endpoint. Deliberately separate from BuildSummary
    // rather than shoehorned into it: Claude usage events need no host-attribution join (Claude
    // Code's own JSONL already carries timestamp + model + usage per record - see
    // ClaudeSessionLogScanner), and Claude's cost math has no best/worst-case range (see
    // ClaudeApiCostCalculator), so TodayCostHigh/SevenDayCostHigh/MonthCostHigh are simply set
    // equal to the non-High figures to slot into the same CodexApiUsageSummary shape the rest of
    // the app (panel view model, cache, widget) already knows how to render.
    private CodexApiUsageSummary BuildClaudeSummary(
        CodexApiEndpointSettings endpoint,
        int claudeEndpointCount,
        IReadOnlyDictionary<string, int> claudeRegionCounts)
    {
        var now = DateTimeOffset.Now;
        var todayStart = new DateTimeOffset(now.Date, now.Offset);
        var sevenDayStart = todayStart.AddDays(-6);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        var effectiveMonthStart = monthStart > endpoint.TrackFrom ? monthStart : endpoint.TrackFrom;
        var normalizedRegion = endpoint.AwsRegion.Trim().ToLowerInvariant();

        decimal todayCost = 0, sevenDayCost = 0, monthCost = 0;
        long inputTokens = 0, cachedInputTokens = 0, cacheWriteTokens = 0, outputTokens = 0;
        var costByModel = new Dictionary<string, decimal>();
        var requestCount = 0;
        var pricingUnavailable = false;

        foreach (var usageEvent in _claudeUsageEvents.Values)
        {
            // Region-based attribution heuristic (see class remarks on ClaudeSessionLogScanner for
            // why this is heuristic in the first place):
            //   - Exactly one Claude Bedrock endpoint configured -> attribute everything detected
            //     to it, regardless of whether/what region was detected. This covers the common
            //     case (one Claude Code install, one AWS account) without requiring the user to
            //     fill in AwsRegion at all.
            //   - Two or more configured -> only attribute events whose detected region matches
            //     this endpoint's AwsRegion exactly; events with no detected region, or whose
            //     region maps to more than one configured endpoint, are dropped rather than risk
            //     double- or mis-counting them (same conservative principle as Codex's
            //     ambiguous-host handling).
            if (claudeEndpointCount > 1)
            {
                if (normalizedRegion.Length == 0 || usageEvent.Region.Length == 0)
                {
                    continue;
                }

                if (usageEvent.Region != normalizedRegion)
                {
                    continue;
                }

                if (claudeRegionCounts.GetValueOrDefault(normalizedRegion) > 1)
                {
                    continue;
                }
            }

            if (usageEvent.Timestamp < endpoint.TrackFrom)
            {
                continue;
            }

            var localTimestamp = usageEvent.Timestamp.ToLocalTime();
            if (localTimestamp < effectiveMonthStart)
            {
                continue;
            }

            decimal cost = 0;
            if (_claudePricingRegistry is not null &&
                _claudePricingRegistry.TryGetEffectivePricing(endpoint, usageEvent.Model, usageEvent.RawModelId, usageEvent.Timestamp, out var pricing))
            {
                cost = ClaudeApiCostCalculator.Calculate(usageEvent, pricing);
            }
            else
            {
                pricingUnavailable = true;
            }

            monthCost += cost;
            if (localTimestamp >= sevenDayStart)
            {
                sevenDayCost += cost;
            }

            if (localTimestamp >= todayStart)
            {
                todayCost += cost;
            }

            inputTokens += usageEvent.InputTokens;
            cachedInputTokens += usageEvent.CachedInputTokens;
            cacheWriteTokens += usageEvent.CacheWriteInputTokens;
            outputTokens += usageEvent.OutputTokens;
            costByModel[usageEvent.Model] = costByModel.GetValueOrDefault(usageEvent.Model) + cost;
            requestCount++;
        }

        double? budgetPercent = endpoint.MonthlyBudget is > 0
            ? (double)(monthCost / endpoint.MonthlyBudget.Value * 100)
            : null;

        _logger.LogInformation(
            "[CodexApiCost] Claude endpoint '{Name}' region={Region}: {RequestCount} requests counted, " +
            "PricingUnavailable={PricingUnavailable}, Month={MonthCost} 7D={SevenDayCost} Today={TodayCost}",
            endpoint.Name,
            endpoint.AwsRegion,
            requestCount,
            pricingUnavailable,
            monthCost,
            sevenDayCost,
            todayCost);

        return new CodexApiUsageSummary
        {
            EndpointId = endpoint.Id,
            Name = endpoint.Name,
            TodayCost = todayCost,
            SevenDayCost = sevenDayCost,
            MonthCost = monthCost,
            TodayCostHigh = todayCost,
            SevenDayCostHigh = sevenDayCost,
            MonthCostHigh = monthCost,
            MonthlyBudget = endpoint.MonthlyBudget,
            MonthlyBudgetPercent = budgetPercent,
            PricingUnavailable = pricingUnavailable,
            ShowInWidget = endpoint.ShowInWidget,
            TurnCount = requestCount,
            RequestCount = requestCount,
            InputTokens = inputTokens,
            CachedInputTokens = cachedInputTokens,
            CacheWriteInputTokens = cacheWriteTokens,
            OutputTokens = outputTokens,
            CostByModel = costByModel
        };
    }

    private void PruneOldData(bool pruneCodex, bool pruneClaude)
    {
        var cutoff = DateTimeOffset.UtcNow - CodexApiCostCache.RetentionWindow;

        if (pruneCodex)
        {
            foreach (var key in _attributions.Where(pair => pair.Value.LastSeenAt < cutoff).Select(pair => pair.Key).ToList())
            {
                _attributions.Remove(key);
            }

            foreach (var key in _usageEvents.Where(pair => pair.Value.Timestamp < cutoff).Select(pair => pair.Key).ToList())
            {
                _usageEvents.Remove(key);
            }
        }

        if (!pruneClaude)
        {
            return;
        }

        foreach (var key in _claudeUsageEvents.Where(pair => pair.Value.Timestamp < cutoff).Select(pair => pair.Key).ToList())
        {
            _claudeUsageEvents.Remove(key);
        }
    }

    private void PersistScanArtifacts(bool persistCodex, bool persistClaude)
    {
        if (!persistCodex && !persistClaude)
        {
            return;
        }

        if (persistCodex)
        {
            _cache.SaveAttributions(_attributions);
            _cache.SaveUsageEvents(_usageEvents);
        }

        if (persistClaude)
        {
            _cache.SaveClaudeUsageEvents(_claudeUsageEvents);
        }

        // Written last, and deliberately so: these are separate files, and if the process dies
        // between them the survivable failure is offsets that lag the event stores (the next scan
        // re-reads a little and overwrites events by key) rather than offsets that run ahead of
        // them, which would silently drop history.
        _cache.SaveScanState(_scanState);
    }
}
