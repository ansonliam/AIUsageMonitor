using System.Net;
using System.Net.Http;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class GitHubReleaseCacheTests
{
    [TestMethod]
    public async Task ExpiredLatestCache_IsReplacedBySuccessfulGitHubResponse()
    {
        var root = CreateRoot();
        try
        {
            var cache = new GitHubReleaseCacheStore(root);
            // Cached as critical so a 2-day-old cache is expired under the daily cadence - the
            // default (non-critical) cadence is weekly, see GitHubReleaseCacheSeverityTests.
            cache.Save(new GitHubReleaseCacheEntry(
                DateTimeOffset.UtcNow.AddDays(-2), "v1.0.0", "https://github.com/ansonliam/AIUsageMonitor/releases/tag/v1.0.0", null,
                IsLatestReleaseCritical: true));
            var requests = 0;
            var service = CreateService(root, cache, _ =>
            {
                requests++;
                return JsonResponse("""{ "tag_name": "v1.0.1", "html_url": "https://github.com/ansonliam/AIUsageMonitor/releases/tag/v1.0.1" }""");
            });

            var result = await service.CheckAsync();

            Assert.AreEqual(1, requests);
            Assert.AreEqual("v1.0.1", result.LatestReleaseTag);
            Assert.IsFalse(result.IsCached);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task FreshCache_IsReusedForLatestAndReleaseHistory()
    {
        var root = CreateRoot();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var cachedHistory = new[] { new GitHubRelease("Version 1.0.1", "v1.0.1", "1 August 2026", ["Cached change"]) };
            var cache = new GitHubReleaseCacheStore(root);
            cache.Save(new GitHubReleaseCacheEntry(
                now, "v1.0.1", "https://github.com/ansonliam/AIUsageMonitor/releases/tag/v1.0.1", null, now, cachedHistory));
            var requests = 0;
            var service = CreateService(root, cache, _ =>
            {
                requests++;
                throw new InvalidOperationException("A fresh cache should prevent this request.");
            });

            var latest = await service.CheckAsync();
            var history = await service.GetRecentReleasesAsync();

            Assert.AreEqual(0, requests);
            Assert.IsTrue(latest.IsCached);
            Assert.AreEqual(cachedHistory[0].Tag, history[0].Tag);
            CollectionAssert.AreEqual(cachedHistory[0].ChangeTitles.ToArray(), history[0].ChangeTitles.ToArray());
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task ForcedReleaseHistoryRefresh_ReplacesFreshHistory()
    {
        var root = CreateRoot();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var cachedHistory = new[] { new GitHubRelease("Version 1.0.1", "v1.0.1", "1 August 2026", ["Cached change"]) };
            var cache = new GitHubReleaseCacheStore(root);
            cache.Save(new GitHubReleaseCacheEntry(
                now, "v1.0.1", "https://github.com/ansonliam/AIUsageMonitor/releases/tag/v1.0.1", null, now, cachedHistory));
            var requests = 0;
            var service = CreateService(root, cache, _ =>
            {
                requests++;
                return JsonResponse("""
                    [{ "tag_name": "v1.0.2", "name": "Version 1.0.2", "body": "## Changes\n- Fresh change", "published_at": "2026-08-26T00:00:00Z" }]
                    """);
            });

            var history = await service.GetRecentReleasesAsync(force: true);

            Assert.AreEqual(1, requests);
            Assert.AreEqual("v1.0.2", history[0].Tag);
            CollectionAssert.AreEqual(new[] { "Fresh change" }, history[0].ChangeTitles.ToArray());
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task ReleaseBodyWithCriticalSeverity_IsParsedAndCached()
    {
        var root = CreateRoot();
        try
        {
            var cache = new GitHubReleaseCacheStore(root);
            var service = CreateService(root, cache, _ => JsonResponse("""
                { "tag_name": "v1.0.1", "html_url": "https://github.com/ansonliam/AIUsageMonitor/releases/tag/v1.0.1", "body": "## Severity\nCritical\n\n## Changes\n- Fix" }
                """));

            var result = await service.CheckAsync();

            Assert.IsTrue(result.IsCritical);
            Assert.IsTrue(cache.Load()!.IsLatestReleaseCritical);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task ReleaseBodyWithoutSeverityMarker_DefaultsToNotCritical()
    {
        var root = CreateRoot();
        try
        {
            var cache = new GitHubReleaseCacheStore(root);
            var service = CreateService(root, cache, _ => JsonResponse("""
                { "tag_name": "v1.0.1", "html_url": "https://github.com/ansonliam/AIUsageMonitor/releases/tag/v1.0.1", "body": "## Changes\n- Fix" }
                """));

            var result = await service.CheckAsync();

            Assert.IsFalse(result.IsCritical);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task NonCriticalCache_StaysFreshWithinAWeek()
    {
        var root = CreateRoot();
        try
        {
            var cache = new GitHubReleaseCacheStore(root);
            cache.Save(new GitHubReleaseCacheEntry(
                DateTimeOffset.UtcNow.AddDays(-2), "v1.0.0", "https://github.com/ansonliam/AIUsageMonitor/releases/tag/v1.0.0", null,
                IsLatestReleaseCritical: false));
            var requests = 0;
            var service = CreateService(root, cache, _ =>
            {
                requests++;
                throw new InvalidOperationException("A cache within the weekly cadence should prevent this request.");
            });

            var result = await service.CheckAsync();

            Assert.AreEqual(0, requests);
            Assert.IsTrue(result.IsCached);
            Assert.AreEqual("v1.0.0", result.LatestReleaseTag);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task NonCriticalCache_ExpiresAfterAWeek()
    {
        var root = CreateRoot();
        try
        {
            var cache = new GitHubReleaseCacheStore(root);
            cache.Save(new GitHubReleaseCacheEntry(
                DateTimeOffset.UtcNow.AddDays(-8), "v1.0.0", "https://github.com/ansonliam/AIUsageMonitor/releases/tag/v1.0.0", null,
                IsLatestReleaseCritical: false));
            var requests = 0;
            var service = CreateService(root, cache, _ =>
            {
                requests++;
                return JsonResponse("""{ "tag_name": "v1.0.1", "html_url": "https://github.com/ansonliam/AIUsageMonitor/releases/tag/v1.0.1" }""");
            });

            var result = await service.CheckAsync();

            Assert.AreEqual(1, requests);
            Assert.AreEqual("v1.0.1", result.LatestReleaseTag);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task RateLimitedOrOfflineRequest_FallsBackToExpiredSuccessfulCache()
    {
        var root = CreateRoot();
        try
        {
            var old = DateTimeOffset.UtcNow.AddDays(-2);
            var cachedHistory = new[] { new GitHubRelease("Version 1.0.0", "v1.0.0", "1 July 2026", ["Cached change"]) };
            var cache = new GitHubReleaseCacheStore(root);
            // Cached as critical so the 2-day-old cache is expired (daily cadence) and CheckAsync
            // actually reaches the stubbed 403 response below, rather than short-circuiting on the
            // default weekly cadence and never exercising the rate-limit fallback this test covers.
            cache.Save(new GitHubReleaseCacheEntry(
                old, "v1.0.0", "https://github.com/ansonliam/AIUsageMonitor/releases/tag/v1.0.0", null, old, cachedHistory,
                IsLatestReleaseCritical: true));
            var reset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString();
            var service = CreateService(root, cache, _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
                response.Headers.Add("X-RateLimit-Reset", reset);
                return response;
            });

            var latest = await service.CheckAsync();
            var history = await service.GetRecentReleasesAsync();

            Assert.IsTrue(latest.IsAvailable);
            Assert.IsTrue(latest.IsCached);
            Assert.AreEqual("v1.0.0", latest.LatestReleaseTag);
            Assert.IsNotNull(latest.NextCheckAfterUtc);
            Assert.AreEqual(cachedHistory[0].Tag, history[0].Tag);
            CollectionAssert.AreEqual(cachedHistory[0].ChangeTitles.ToArray(), history[0].ChangeTitles.ToArray());
        }
        finally { DeleteRoot(root); }
    }

    private static GitHubReleaseService CreateService(string root, GitHubReleaseCacheStore cache, Func<HttpRequestMessage, HttpResponseMessage> send)
        => new(new StubHttpClientFactory(send), new DeveloperModeSettingsStore(root), cache);

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static string CreateRoot() => Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> send) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(send));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
