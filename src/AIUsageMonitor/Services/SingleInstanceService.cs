using System.IO.Pipes;
using System.IO;
using System.Text;

namespace AIUsageMonitor.Services;

public sealed class SingleInstanceService : IDisposable
{
    public const string PipeName = "AIUsageMonitor.HookNotifications.v1";
    private const string MutexName = "Local\\AIUsageMonitor.PrimaryInstance.v1";
    private Mutex? _mutex;
    private bool _ownsMutex;

    public bool TryAcquirePrimaryInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _ownsMutex = createdNew;
        return createdNew;
    }

    public async Task<bool> SendNotificationAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(timeout.Token);

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(message.AsMemory(), timeout.Token);
            var response = await reader.ReadLineAsync(timeout.Token);
            return string.Equals(response, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            _mutex?.ReleaseMutex();
        }

        _mutex?.Dispose();
        _mutex = null;
        _ownsMutex = false;
    }
}
