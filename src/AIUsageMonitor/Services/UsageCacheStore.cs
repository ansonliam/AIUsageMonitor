using System.IO;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class UsageCacheStore
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIUsageMonitor",
        "usage-cache.json");
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };
    private readonly object _syncRoot = new();
    private Dictionary<string, UsageSnapshot>? _snapshots;

    public IReadOnlyList<UsageSnapshot> Load()
    {
        lock (_syncRoot)
        {
            EnsureLoaded();
            return _snapshots!.Values.ToArray();
        }
    }

    public void Save(UsageSnapshot snapshot)
    {
        if (snapshot.Status != UsageStatus.Available)
        {
            return;
        }

        lock (_syncRoot)
        {
            EnsureLoaded();
            _snapshots![snapshot.Provider] = snapshot;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                var temporaryPath = CachePath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(_snapshots.Values, SerializerOptions));
                File.Move(temporaryPath, CachePath, overwrite: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_snapshots is not null)
        {
            return;
        }

        try
        {
            var snapshots = File.Exists(CachePath)
                ? JsonSerializer.Deserialize<List<UsageSnapshot>>(File.ReadAllText(CachePath)) ?? []
                : [];
            _snapshots = snapshots
                .Where(snapshot => snapshot.Status == UsageStatus.Available)
                .GroupBy(snapshot => snapshot.Provider)
                .ToDictionary(group => group.Key, group => group.MaxBy(snapshot => snapshot.RetrievedAt)!);
        }
        catch (JsonException)
        {
            _snapshots = new Dictionary<string, UsageSnapshot>();
        }
        catch (IOException)
        {
            _snapshots = new Dictionary<string, UsageSnapshot>();
        }
        catch (UnauthorizedAccessException)
        {
            _snapshots = new Dictionary<string, UsageSnapshot>();
        }
    }
}
