using System.IO;
using System.Text.RegularExpressions;
using AIUsageMonitor.Models;
using Microsoft.Data.Sqlite;

namespace AIUsageMonitor.Services;

// Reads Codex's own local runtime log (a SQLite file Codex itself writes and rotates) to find
// which endpoint host each turn actually sent HTTP requests to. Codex's `feedback_log_body`
// column is plain tracing-span text, not JSON, e.g.:
// "...turn{... turn.id=01a0... model=gpt-5.6-terra ...}:...run_sampling_request{turn_id=01a0... model=gpt-5.6-terra ...}:...: Request completed method=POST url=https://host/openai/v1/responses status=200 ..."
// Rows with no turn id (list_models, analytics-events, etc.) carry no endpoint evidence for any
// turn and are skipped entirely - they must never be counted.
public sealed partial class CodexRuntimeLogScanner
{
    private readonly string _databasePath;

    public CodexRuntimeLogScanner(string? rootDirectory = null)
    {
        var root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
        _databasePath = Path.Combine(root, "logs_2.sqlite");
    }

    // Stop at whitespace, ':', or '}' - the delimiters between tracing-span key=value tags - rather
    // than assuming a specific turn id format (currently a UUIDv7, but that's not guaranteed).
    [GeneratedRegex(@"turn[_.]id=([^\s:}]+)")]
    private static partial Regex TurnIdPattern();

    [GeneratedRegex(@"model=([^\s:}]+)")]
    private static partial Regex ModelPattern();

    [GeneratedRegex(@"url=(\S+)")]
    private static partial Regex UrlPattern();

    // Returns new requests with id > lastProcessedId, the new checkpoint to persist, and how many
    // candidate rows (target/body matched, before turn-id extraction) were scanned - a diagnostic
    // signal for telling "nothing in the log" apart from "rows present but couldn't be parsed".
    // If the log was rotated/recreated (lastProcessedId is now beyond the current max id),
    // the checkpoint resets to 0 and the caller receives a full rescan.
    public (IReadOnlyList<CodexRuntimeRequest> Requests, long NewCheckpoint, int RowsScanned) ScanNew(long lastProcessedId)
    {
        if (!File.Exists(_databasePath))
        {
            return ([], lastProcessedId, 0);
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var checkpoint = lastProcessedId;
            using (var maxCommand = connection.CreateCommand())
            {
                maxCommand.CommandText = "SELECT MAX(id) FROM logs";
                var maxIdResult = maxCommand.ExecuteScalar();
                var maxId = maxIdResult is long value ? value : 0;
                if (lastProcessedId > maxId)
                {
                    // The log file was rotated or recreated underneath us; rescan from the start.
                    checkpoint = 0;
                }
            }

            var requests = new List<CodexRuntimeRequest>();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, ts, feedback_log_body FROM logs " +
                "WHERE id > @lastId AND target = 'codex_http_client::client' " +
                "AND feedback_log_body LIKE '%Request completed%' " +
                "ORDER BY id";
            command.Parameters.AddWithValue("@lastId", checkpoint);

            using var reader = command.ExecuteReader();
            var newCheckpoint = checkpoint;
            var rowsScanned = 0;
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var ts = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
                var body = reader.IsDBNull(2) ? "" : reader.GetString(2);
                newCheckpoint = id;
                rowsScanned++;

                var request = ParseRequest(id, ts, body);
                if (request is not null)
                {
                    requests.Add(request);
                }
            }

            return (requests, newCheckpoint, rowsScanned);
        }
        catch (SqliteException)
        {
            return ([], lastProcessedId, 0);
        }
        catch (IOException)
        {
            return ([], lastProcessedId, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return ([], lastProcessedId, 0);
        }
    }

    private static CodexRuntimeRequest? ParseRequest(long id, long unixSeconds, string body)
    {
        var turnMatch = TurnIdPattern().Match(body);
        var urlMatch = UrlPattern().Match(body);
        if (!turnMatch.Success || !urlMatch.Success)
        {
            // No endpoint evidence for any turn (e.g. list_models, analytics-events) - ignore.
            return null;
        }

        if (!Uri.TryCreate(urlMatch.Groups[1].Value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var modelMatch = ModelPattern().Match(body);
        var apiPath = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault();

        return new CodexRuntimeRequest
        {
            Sequence = id,
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds),
            TurnId = turnMatch.Groups[1].Value,
            Model = modelMatch.Success ? modelMatch.Groups[1].Value : null,
            Url = uri,
            ApiPath = string.IsNullOrEmpty(apiPath) ? null : apiPath
        };
    }
}
