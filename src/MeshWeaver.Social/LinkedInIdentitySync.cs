using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MeshWeaver.Social;

/// <summary>
/// Merging LinkedIn's <c>userinfo</c> identity (display name + profile photo) into a social
/// profile's stored content — the pure half of "sync my profile from the account", split out so
/// the merge rules are unit-testable without an OAuth round trip.
///
/// <para>The rule is READ-MERGE-WRITE, never replace: LinkedIn owns the display name and the
/// photo, the mesh owns everything else (network, headline, owner, handle, profile URL). A
/// wholesale content write would silently drop those.</para>
/// </summary>
public static class LinkedInIdentitySync
{
    /// <summary>Content key of the profile's display name.</summary>
    public const string DisplayNameKey = "displayName";

    /// <summary>Content key of the profile's avatar.</summary>
    public const string ImageUrlKey = "imageUrl";

    /// <summary>
    /// The profile content with LinkedIn's <paramref name="displayName"/> and
    /// <paramref name="pictureUrl"/> applied over whatever is already stored. Reads the existing
    /// content in WHATEVER shape it arrives — a typed record, a <see cref="JsonElement"/>, or a
    /// dictionary — because content typing depends on which hub last touched it. A null or blank
    /// incoming value leaves the stored one alone: an account that exposes no photo must not
    /// erase a good one. Pure.
    /// </summary>
    public static Dictionary<string, object?> Merge(object? existingContent, string? displayName, string? pictureUrl)
    {
        var merged = ToDictionary(existingContent);
        merged["$type"] = "SocialProfile";
        if (!string.IsNullOrWhiteSpace(displayName))
            merged[DisplayNameKey] = displayName!.Trim();
        if (!string.IsNullOrWhiteSpace(pictureUrl))
            merged[ImageUrlKey] = pictureUrl!.Trim();
        return merged;
    }

    /// <summary>
    /// Content as a camelCase property bag, whatever shape it arrived in. An unreadable or absent
    /// content yields an empty bag rather than throwing — the caller still gets a valid profile
    /// carrying the LinkedIn identity. Pure.
    /// </summary>
    public static Dictionary<string, object?> ToDictionary(object? content)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        switch (content)
        {
            case null:
                return result;

            case IDictionary<string, object?> dict:
                foreach (var kv in dict)
                    result[kv.Key] = kv.Value;
                return result;

            case JsonElement { ValueKind: JsonValueKind.Object } element:
                foreach (var property in element.EnumerateObject())
                    result[property.Name] = Unwrap(property.Value);
                return result;

            default:
                // A typed record (the hub's own content type): round-trip through camelCase JSON —
                // the casing the mesh stores and every reader expects.
                try
                {
                    var json = JsonSerializer.Serialize(content, content.GetType(), CamelCase);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var property in doc.RootElement.EnumerateObject())
                            result[property.Name] = Unwrap(property.Value);
                }
                catch (Exception)
                {
                    // Unserializable content must not break a login callback.
                }
                return result;
        }
    }

    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static object? Unwrap(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.Clone(),
    };
}
