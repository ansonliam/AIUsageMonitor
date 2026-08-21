using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Services;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly ILogger<CodexAppServerClient> _logger;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private StreamWriter? _writer;
    private Task? _readerTask;
    private long _nextId;
    private bool _initialized;

    public CodexAppServerClient(ILogger<CodexAppServerClient> logger)
    {
        _logger = logger;
    }

    public async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await SendRequestCoreAsync(method, parameters, cancellationToken);
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_initialized && _process is { HasExited: false })
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized && _process is { HasExited: false })
            {
                return;
            }

            await StopProcessAsync();
            var executable = FindCodexExecutable();
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // This only overrides a legacy value that newer Codex builds no longer accept.
            // It is process-local and never changes the user's config file.
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("service_tier=\"fast\"");
            startInfo.ArgumentList.Add("app-server");

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!_process.Start())
            {
                throw new CodexAppServerException("Codex could not be started.");
            }

            _writer = _process.StandardInput;
            _readerTask = ReadResponsesAsync(_process.StandardOutput, _lifetime.Token);
            _ = DrainStandardErrorAsync(_process.StandardError, _lifetime.Token);

            await SendRequestCoreAsync(
                "initialize",
                new
                {
                    clientInfo = new
                    {
                        name = "ai_usage_monitor",
                        title = "AI Usage Monitor",
                        version = "0.1.0"
                    }
                },
                cancellationToken);

            await SendNotificationAsync("initialized", new { }, cancellationToken);
            _initialized = true;
            _logger.LogInformation("Codex app-server connection initialized");
        }
        catch
        {
            await StopProcessAsync();
            throw;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task<JsonElement> SendRequestCoreAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            await WriteMessageAsync(new { method, id, @params = parameters }, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodexAppServerException("Codex did not respond in time.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task SendNotificationAsync(string method, object parameters, CancellationToken cancellationToken) =>
        WriteMessageAsync(new { method, @params = parameters }, cancellationToken);

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        if (_writer is null)
        {
            throw new CodexAppServerException("Codex is unavailable.");
        }

        var json = JsonSerializer.Serialize(message);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _writer.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new CodexAppServerException("The Codex connection closed unexpectedly.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadResponsesAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
                {
                    continue;
                }

                if (!_pending.TryGetValue(id, out var completion))
                {
                    continue;
                }

                if (root.TryGetProperty("error", out _))
                {
                    completion.TrySetException(new CodexAppServerException("Codex rejected the request."));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    completion.TrySetResult(result.Clone());
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _logger.LogWarning("Codex app-server response stream stopped");
        }
        finally
        {
            _initialized = false;
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(new CodexAppServerException("The Codex connection closed unexpectedly."));
            }
        }
    }

    private async Task DrainStandardErrorAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && await reader.ReadLineAsync(cancellationToken) is not null)
            {
                // Deliberately discard raw process output: it may contain account details.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string FindCodexExecutable()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var codexBin = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        if (Directory.Exists(codexBin))
        {
            var candidate = Directory.EnumerateFiles(codexBin, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return "codex.exe";
    }

    private async Task StopProcessAsync()
    {
        _initialized = false;
        if (_writer is not null)
        {
            await _writer.DisposeAsync();
            _writer = null;
        }

        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
        _process = null;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await StopProcessAsync();
        if (_readerTask is not null)
        {
            try
            {
                await _readerTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetime.Dispose();
        _startLock.Dispose();
        _writeLock.Dispose();
    }
}
