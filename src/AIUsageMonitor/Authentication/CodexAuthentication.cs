using System.Diagnostics;
using System.Text.Json;
using AIUsageMonitor.Services;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Authentication;

public sealed class CodexAuthentication : IProviderAuthentication
{
    private readonly CodexAppServerClient _client;
    private readonly ILogger<CodexAuthentication> _logger;

    public CodexAuthentication(CodexAppServerClient client, ILogger<CodexAuthentication> logger)
    {
        _client = client;
        _logger = logger;
    }

    public bool IsAuthenticated { get; private set; }

    public async Task RefreshAuthenticationStateAsync(CancellationToken cancellationToken = default)
    {
        var result = await _client.SendRequestAsync(
            "account/read",
            new { refreshToken = false },
            cancellationToken);

        IsAuthenticated = result.TryGetProperty("account", out var account) &&
                          account.ValueKind == JsonValueKind.Object &&
                          account.TryGetProperty("type", out var type) &&
                          string.Equals(type.GetString(), "chatgpt", StringComparison.OrdinalIgnoreCase);

        if (!IsAuthenticated)
        {
            _logger.LogInformation("Codex ChatGPT authentication is required");
        }
    }

    public async Task StartLoginAsync(CancellationToken cancellationToken = default)
    {
        var result = await _client.SendRequestAsync(
            "account/login/start",
            new { type = "chatgpt", useHostedLoginSuccessPage = true, appBrand = "codex" },
            cancellationToken);

        if (!result.TryGetProperty("authUrl", out var urlElement) ||
            !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var url) ||
            url.Scheme is not ("https" or "http"))
        {
            throw new CodexAppServerException("Codex did not provide a login page.");
        }

        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }
}
