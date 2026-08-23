using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Integrations;

public sealed partial class AntigravityLanguageServerClient
{
    private const string RpcPath = "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
    private readonly ILogger<AntigravityLanguageServerClient> _logger;

    public AntigravityLanguageServerClient(ILogger<AntigravityLanguageServerClient> logger)
    {
        _logger = logger;
    }

    public async Task<JsonElement> RetrieveUserQuotaSummaryAsync(
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        var server = FindRunningServer();
        if (server is null)
        {
            throw new AntigravityClientException(
                AntigravityFailureKind.ClientNotRunning,
                "Open Antigravity and sign in to refresh usage.");
        }

        var candidatePorts = ResolveCandidatePorts(server);
        if (candidatePorts.Count == 0)
        {
            _logger.LogWarning(
                "Antigravity language server (PID {ProcessId}) exposed no reachable loopback port",
                server.ProcessId);
            throw new AntigravityClientException(
                AntigravityFailureKind.ClientNotRunning,
                "Open Antigravity and sign in to refresh usage.");
        }

        Exception? lastTransportError = null;
        foreach (var port in candidatePorts)
        {
            try
            {
                return await SendQuotaRequestAsync(port, server.CsrfToken, forceRefresh, cancellationToken);
            }
            catch (AntigravityClientException)
            {
                // Authentication, rate limiting, and unparseable responses come from the real
                // server and are definitive — do not keep probing other ports.
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                lastTransportError = exception;
            }
            catch (AuthenticationException exception)
            {
                // The non-TLS sidecar port rejects the HTTPS handshake; try the next candidate.
                lastTransportError = exception;
            }
            catch (IOException exception)
            {
                lastTransportError = exception;
            }
        }

        _logger.LogWarning(
            lastTransportError,
            "Antigravity quota RPC failed on all {Count} candidate port(s)",
            candidatePorts.Count);
        throw new AntigravityClientException(
            AntigravityFailureKind.Error,
            "Unable to retrieve Antigravity usage.");
    }

    private async Task<JsonElement> SendQuotaRequestAsync(
        int port,
        string csrfToken,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                request.RequestUri?.IsLoopback == true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"https://127.0.0.1:{port}{RpcPath}"));
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        request.Headers.TryAddWithoutValidation("x-codeium-csrf-token", csrfToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(
            forceRefresh ? "{\"request\":{},\"forceRefresh\":true}" : "{\"request\":{}}",
            Encoding.UTF8,
            "application/json");

        _logger.LogInformation(
            "Provider API call started | Provider=Google Antigravity | API=RetrieveUserQuotaSummary RPC | Port={Port} | ForceRefresh={ForceRefresh}",
            port,
            forceRefresh);
        using var response = await client.SendAsync(request, cancellationToken);
        _logger.LogInformation(
            "Provider API call completed | Provider=Google Antigravity | API=RetrieveUserQuotaSummary RPC | Port={Port} | StatusCode={StatusCode} | DurationMs={DurationMs}",
            port,
            (int)response.StatusCode,
            System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AntigravityClientException(
                AntigravityFailureKind.AuthenticationRequired,
                "Sign in to Antigravity to refresh usage.");
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new AntigravityClientException(
                AntigravityFailureKind.RateLimited,
                "Antigravity usage is temporarily rate limited.");
        }

        // Any other non-success status means this port is not the quota RPC endpoint;
        // let the HttpRequestException bubble so the caller can try the next candidate.
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new AntigravityClientException(
                AntigravityFailureKind.Error,
                "Antigravity returned an unsupported quota response.",
                exception);
        }
    }

    private IReadOnlyList<int> ResolveCandidatePorts(AntigravityServerProcess server)
    {
        // Older Antigravity builds advertise a concrete port on the command line.
        if (server.AdvertisedPort > 0)
        {
            return [server.AdvertisedPort];
        }

        // Current builds pass --https_server_port 0 and let the OS assign a dynamic port,
        // so discover it from the process's listening loopback sockets instead.
        try
        {
            var ports = TcpListenerTable.GetListeningPorts(server.ProcessId);
            _logger.LogInformation(
                "Antigravity advertised a dynamic port; discovered {Count} loopback listener(s) for PID {ProcessId}",
                ports.Count,
                server.ProcessId);
            return ports;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Unable to enumerate Antigravity loopback listeners for PID {ProcessId}",
                server.ProcessId);
            return [];
        }
    }

    internal static AntigravityServerConnection? ParseConnection(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine) ||
            !commandLine.Contains("antigravity", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var csrfToken = ReadArgument(commandLine, "csrf_token");
        if (string.IsNullOrWhiteSpace(csrfToken))
        {
            return null;
        }

        // The port is 0 (or omitted) when the server binds a dynamic port; callers resolve the
        // real port from the OS listener table, so a zero here is valid rather than a failure.
        var portText = ReadArgument(commandLine, "https_server_port");
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            port = 0;
        }

        return port is >= 0 and <= 65535
            ? new AntigravityServerConnection(port, csrfToken)
            : null;
    }

    private AntigravityServerProcess? FindRunningServer()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'language_server.exe'");
            using var results = searcher.Get();
            foreach (ManagementBaseObject process in results)
            {
                var connection = ParseConnection(process["CommandLine"] as string);
                if (connection is null)
                {
                    continue;
                }

                var processId = Convert.ToInt32(process["ProcessId"], CultureInfo.InvariantCulture);
                return new AntigravityServerProcess(processId, connection.Port, connection.CsrfToken);
            }
        }
        catch (ManagementException exception)
        {
            _logger.LogWarning(exception, "Unable to inspect the running Antigravity language server");
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "Access to the running Antigravity language server was denied");
        }

        return null;
    }

    private static string? ReadArgument(string commandLine, string argumentName)
    {
        var match = ArgumentRegex().Match(commandLine, 0);
        while (match.Success)
        {
            if (string.Equals(match.Groups["name"].Value, argumentName, StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups["quoted"].Success
                    ? match.Groups["quoted"].Value
                    : match.Groups["plain"].Value;
            }

            match = match.NextMatch();
        }

        return null;
    }

    [GeneratedRegex(
        "(?:^|\\s)--(?<name>[A-Za-z0-9_]+)(?:=|\\s+)(?:\\\"(?<quoted>[^\\\"]*)\\\"|(?<plain>[^\\s]+))",
        RegexOptions.CultureInvariant)]
    private static partial Regex ArgumentRegex();
}

internal sealed record AntigravityServerConnection(int Port, string CsrfToken);

internal sealed record AntigravityServerProcess(int ProcessId, int AdvertisedPort, string CsrfToken);

/// <summary>
/// Enumerates the IPv4 TCP ports a specific process is listening on, using the Windows
/// extended TCP table so a dynamically-assigned Antigravity port can be discovered.
/// </summary>
internal static class TcpListenerTable
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    private const uint IpLoopback = 0x0100007F; // 127.0.0.1 in network byte order
    private const uint IpAny = 0x00000000;      // 0.0.0.0

    public static IReadOnlyList<int> GetListeningPorts(int processId)
    {
        var ports = new List<int>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size <= 0)
        {
            return ports;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
            {
                return ports;
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var rowPtr = buffer + sizeof(int);
            var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            for (var i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                rowPtr += rowSize;

                if (row.OwningPid != (uint)processId ||
                    row.LocalAddr is not (IpLoopback or IpAny))
                {
                    continue;
                }

                // LocalPort is stored in network byte order within the low 16 bits.
                var port = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                if (port is > 0 and <= 65535 && !ports.Contains(port))
                {
                    ports.Add(port);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        ports.Sort();
        return ports;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);
}

public enum AntigravityFailureKind
{
    ClientNotRunning,
    AuthenticationRequired,
    RateLimited,
    Error
}

public sealed class AntigravityClientException : Exception
{
    public AntigravityClientException(
        AntigravityFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public AntigravityFailureKind Kind { get; }
}
