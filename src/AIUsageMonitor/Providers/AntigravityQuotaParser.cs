using System.Globalization;
using System.Text;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Providers;

internal static class AntigravityQuotaParser
{
    public static IReadOnlyList<UsageWindowSnapshot> Parse(JsonElement root)
    {
        var quota = UnwrapResponse(root);
        var windows = new List<UsageWindowSnapshot>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (TryGetProperty(quota, out var groups, "groups") && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
            {
                var groupName = ReadString(group, "displayName", "display_name");
                if (TryGetProperty(group, out var buckets, "buckets") && buckets.ValueKind == JsonValueKind.Array)
                {
                    AddBuckets(buckets, groupName, windows, identities);
                }
            }
        }

        if (TryGetProperty(quota, out var legacyBuckets, "buckets") &&
            legacyBuckets.ValueKind == JsonValueKind.Array)
        {
            AddBuckets(legacyBuckets, null, windows, identities);
        }

        return windows;
    }

    private static JsonElement UnwrapResponse(JsonElement element)
    {
        for (var depth = 0; depth < 3 && element.ValueKind == JsonValueKind.Object; depth++)
        {
            if (!TryGetProperty(element, out var response, "response") ||
                response.ValueKind != JsonValueKind.Object)
            {
                break;
            }

            element = response;
        }

        return element;
    }

    private static void AddBuckets(
        JsonElement buckets,
        string? groupName,
        ICollection<UsageWindowSnapshot> windows,
        ISet<string> identities)
    {
        var groupWindows = new List<UsageWindowSnapshot>();

        foreach (var bucket in buckets.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
        {
            if (ReadBoolean(bucket, "disabled") == true ||
                !TryReadRemainingPercent(bucket, out var remainingPercent))
            {
                continue;
            }

            var bucketId = ReadString(bucket, "bucketId", "bucket_id");
            var displayName = ReadString(bucket, "displayName", "display_name");
            var window = ReadString(bucket, "window");
            var windowLabel = GetWindowLabel(window, bucketId, displayName);
            var labelPart = FirstNonEmpty(displayName, window, bucketId, "Usage");
            if (!string.IsNullOrWhiteSpace(window) &&
                !labelPart.Contains(window, StringComparison.OrdinalIgnoreCase))
            {
                labelPart = $"{labelPart} {window}";
            }

            var label = !string.IsNullOrWhiteSpace(groupName) &&
                        !labelPart.Contains(groupName, StringComparison.OrdinalIgnoreCase)
                ? $"{groupName} · {labelPart}"
                : labelPart;
            if (windowLabel is not null)
            {
                label = string.IsNullOrWhiteSpace(groupName) ? windowLabel : $"{groupName} · {windowLabel}";
            }
            var resetAt = ReadResetAt(bucket);
            var identity = $"{groupName}|{bucketId}|{label}|{resetAt:O}";
            if (!identities.Add(identity))
            {
                continue;
            }

            groupWindows.Add(new UsageWindowSnapshot
            {
                Label = label,
                GroupName = groupName,
                WindowLabel = windowLabel,
                RemainingPercent = Math.Clamp(remainingPercent, 0, 100),
                ResetAt = resetAt
            });
        }

        // Preserve unknown bucket labels, but always distinguish known quota periods.
        if (groupWindows.Count == 1 && groupWindows[0].WindowLabel is null &&
            !string.IsNullOrWhiteSpace(groupName))
        {
            groupWindows[0] = groupWindows[0] with { Label = groupName };
        }

        foreach (var window in groupWindows.OrderBy(window => window.WindowLabel switch
                 {
                     "5H" => 0,
                     "W" => 1,
                     _ => 2
                 }))
        {
            windows.Add(window);
        }
    }

    private static string? GetWindowLabel(params string?[] values)
    {
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var normalized = NormalizeName(value!);
            if (normalized.Contains("5h", StringComparison.Ordinal) ||
                normalized.Contains("fivehour", StringComparison.Ordinal))
            {
                return "5H";
            }

            if (normalized.Contains("weekly", StringComparison.Ordinal) || normalized == "week")
            {
                return "W";
            }
        }

        return null;
    }

    private static bool TryReadRemainingPercent(JsonElement bucket, out double remainingPercent)
    {
        if (TryReadNumber(bucket, out var remainingFraction, "remainingFraction", "remaining_fraction"))
        {
            remainingPercent = remainingFraction <= 1 ? remainingFraction * 100 : remainingFraction;
            return double.IsFinite(remainingPercent);
        }

        if (TryReadNumber(
                bucket,
                out var remaining,
                "remainingPercentage",
                "remainingPercent",
                "remaining_percentage",
                "remaining_percent"))
        {
            remainingPercent = remaining <= 1 ? remaining * 100 : remaining;
            return double.IsFinite(remainingPercent);
        }

        if (TryReadNumber(bucket, out var used, "usedPercentage", "usedPercent", "utilization"))
        {
            var usedPercent = used <= 1 ? used * 100 : used;
            remainingPercent = 100 - usedPercent;
            return double.IsFinite(remainingPercent);
        }

        remainingPercent = 0;
        return false;
    }

    private static DateTimeOffset? ReadResetAt(JsonElement bucket)
    {
        if (!TryGetProperty(bucket, out var reset, "resetTime", "reset_time", "resetsAt", "resets_at"))
        {
            return null;
        }

        if (reset.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                reset.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            return timestamp;
        }

        if (reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        if (reset.ValueKind == JsonValueKind.Object &&
            TryReadNumber(reset, out var secondsValue, "seconds") &&
            secondsValue >= long.MinValue && secondsValue <= long.MaxValue)
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)secondsValue);
        }

        return null;
    }

    private static bool? ReadBoolean(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var property, names))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool TryReadNumber(JsonElement element, out double value, params string[] names)
    {
        value = 0;
        if (!TryGetProperty(element, out var property, names))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(
                   property.GetString(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static string? ReadString(JsonElement element, params string[] names) =>
        TryGetProperty(element, out var property, names) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;

    private static bool TryGetProperty(JsonElement element, out JsonElement property, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var normalizedNames = names.Select(NormalizeName).ToHashSet(StringComparer.Ordinal);
            foreach (var candidate in element.EnumerateObject())
            {
                if (normalizedNames.Contains(NormalizeName(candidate.Name)))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static string NormalizeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;
}
