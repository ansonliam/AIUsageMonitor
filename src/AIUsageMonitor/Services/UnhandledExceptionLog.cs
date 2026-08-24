using System.IO;
using System.Text;

namespace AIUsageMonitor.Services;

public sealed class UnhandledExceptionLog : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public UnhandledExceptionLog(string? logDirectory = null)
    {
        LogDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIUsageMonitor",
            "logs");
        LogPath = Path.Combine(LogDirectory, "unhandled-exceptions.log");

        Directory.CreateDirectory(LogDirectory);
        _writer = new StreamWriter(
            new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    public string LogDirectory { get; }
    public string LogPath { get; }

    public void Write(string source, Exception exception)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _writer.WriteLine($"[{DateTimeOffset.Now:O}] Source={source}");
                _writer.WriteLine($"ProcessId={Environment.ProcessId}");
                _writer.WriteLine(exception);
                _writer.WriteLine(new string('-', 80));
            }
            catch (IOException)
            {
                // There is no reliable fallback once the process has reached an
                // unhandled-exception path and the log file cannot be written.
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
        }
    }
}
