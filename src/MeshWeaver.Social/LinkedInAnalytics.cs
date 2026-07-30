using System;
using System.Globalization;
using System.Text.Json;

namespace MeshWeaver.Social;

/// <summary>
/// The PURE parts of LinkedIn's <c>memberCreatorPostAnalytics</c> call — URN encoding and
/// response summing — split out so the awkward bits are unit-testable without an HTTP round trip.
///
/// <para>This endpoint is what finally makes member-post REACH readable: until 2025 impressions
/// existed only for organization pages (<c>organizationalEntityShareStatistics</c>) or in a
/// member's own data-export archive, which is why the older code hard-coded
/// <c>Impressions: 0</c>.</para>
/// </summary>
public static class LinkedInAnalytics
{
    /// <summary>
    /// LinkedIn's <c>entity</c> query parameter for a post URN. The API demands the URN wrapped in
    /// a typed key — <c>(ugc:urn%3Ali%3AugcPost%3A123)</c> for a ugcPost,
    /// <c>(share:urn%3Ali%3Ashare%3A123)</c> for a share — and rejects a bare URN. Returns null
    /// for anything that is not one of those two shapes, so the caller can skip rather than fire a
    /// request that cannot succeed. Pure.
    /// </summary>
    public static string? EntityParameter(string? urn)
    {
        var trimmed = (urn ?? string.Empty).Trim();
        var key = trimmed switch
        {
            _ when trimmed.StartsWith("urn:li:ugcPost:", StringComparison.Ordinal) => "ugc",
            _ when trimmed.StartsWith("urn:li:share:", StringComparison.Ordinal) => "share",
            _ => null,
        };
        return key is null ? null : $"({key}:{Uri.EscapeDataString(trimmed)})";
    }

    /// <summary>
    /// The total across an analytics response's <c>elements</c> — one element for a TOTAL
    /// aggregation, several for DAILY, so summing is correct for both. Missing or malformed
    /// payloads yield 0 rather than throwing: a stats refresh must never take down the caller.
    /// Pure.
    /// </summary>
    public static int SumCounts(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("elements", out var elements)
            || elements.ValueKind != JsonValueKind.Array)
            return 0;

        var total = 0L;
        foreach (var element in elements.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("count", out var count))
                continue;
            total += count.ValueKind switch
            {
                JsonValueKind.Number when count.TryGetInt64(out var l) => l,
                // A count serialized as a string still counts — LinkedIn has shipped both shapes.
                JsonValueKind.String when long.TryParse(
                    count.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
                _ => 0,
            };
        }
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }
}
