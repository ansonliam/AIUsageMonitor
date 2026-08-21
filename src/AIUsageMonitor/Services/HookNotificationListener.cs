using System.IO.Pipes;
using System.IO;
using System.Text;
using AIUsageMonitor.Models;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Services;

public sealed class HookNotificationListener : IAsyncDisposable
{
    private readonly UsageRefreshService _refreshService;
    private readonly IApplicationController _applicationController;
    private readonly ILogger<HookNotificationListener> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _listenerTask;

    public HookNotificationListener(
        UsageRefreshService refreshService,
        IApplicationController applicationController,
        ILogger<HookNotificationListener> logger)
    {
        _refreshService = refreshService;
        _applicationController = applicationController;
        _logger = logger;
    }

    public Task StartAsync()
    {
        _listenerTask ??= ListenAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _lifetime.Cancel();
        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                SingleInstanceService.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = true
                };

                var message = (await reader.ReadLineAsync(cancellationToken))?.Trim().ToLowerInvariant();
                if (message == "open")
                {
                    await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                    _applicationController.ShowMainWindow();
                }
                else if (TryParseProvider(message, out var provider))
                {
                    await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                    _ = _refreshService.RequestRefreshAsync(provider, RefreshReason.Hook);
                }
                else
                {
                    await writer.WriteLineAsync("unsupported".AsMemory(), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                _logger.LogWarning("Hook notification pipe connection failed");
            }
        }
    }

    private static bool TryParseProvider(string? value, out ProviderKind provider)
    {
        provider = value switch
        {
            "codex" => ProviderKind.Codex,
            "claude" => ProviderKind.Claude,
            "antigravity" => ProviderKind.Antigravity,
            _ => default
        };
        return value is "codex" or "claude" or "antigravity";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifetime.Dispose();
    }
}
