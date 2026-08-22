namespace AIUsageMonitor.Services;

public static class CodexEndpointNormalizer
{
    public static bool TryNormalizeHost(string? endpoint, out string normalizedHost)
    {
        normalizedHost = "";
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var candidate = endpoint.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        normalizedHost = uri.Host.ToLowerInvariant();
        return true;
    }

    public static string? TryGetHost(Uri? url) =>
        url is null ? null : url.Host.ToLowerInvariant();
}
