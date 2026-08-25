using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshWeaver.Social;

/// <summary>
/// Building a mesh node's <c>Content</c> so that its <c>$type</c> discriminator SURVIVES the write.
///
/// <para>🚨 <b>The defect this exists to close (issue #52).</b> Handing the mesh a
/// <c>Dictionary&lt;string, object?&gt;</c> as node content — even one carrying an explicit
/// <c>["$type"] = "SocialProfile"</c> entry — silently REWRITES the discriminator. The platform's
/// <c>ObjectPolymorphicConverter.Write</c> takes the discriminator from the CLR type of the value
/// it is given, and while copying the payload's own properties it explicitly SKIPS any property
/// named <c>$type</c>. So the dictionary's entry is dropped and replaced by the dictionary's own
/// collection name, and the node lands in storage as
/// <c>{"$type":"Dictionary`2[String,Object]", …}</c>. Nothing errors. Every typed reader
/// (<c>ContentAs&lt;SocialProfile&gt;</c>) then sees an empty profile: the post card reads
/// "(no author profile)" and the workflow refuses approval for a post whose <c>authorPath</c> is
/// set perfectly well. Observed in production on 2026-08-23 the first time a member connected
/// LinkedIn.</para>
///
/// <para><b>Why the fix is a shape and not a registration.</b> The usual cure for a lost
/// discriminator is <c>WithType(typeof(T), nameof(T))</c> on the hub that reads it. That is
/// impossible here by construction: <c>SocialProfile</c> / <c>SocialPost</c> /
/// <c>LinkedInProfile</c> are content types of DYNAMIC NodeTypes, declared in the mesh
/// (<c>config.WithContentType&lt;SocialProfile&gt;()</c>) and compiled at runtime into a
/// collectible assembly. This compiled module can never reference those CLR types, so the
/// discriminator STRING is the only handle it has on them — and keeping that string intact is
/// therefore the whole contract of a content write from here.</para>
///
/// <para><b>The shape that keeps it.</b> A <see cref="JsonObject"/> is written VERBATIM: the
/// platform's <c>JsonNodeConverter</c> emits the node's existing tree unchanged, and
/// <c>ObjectPolymorphicConverter</c> short-circuits a <see cref="JsonElement"/> the same way.
/// Neither invents a discriminator, neither drops one. So every content this module writes is
/// built HERE, as a <see cref="JsonObject"/>, and never as a dictionary or an anonymous object.</para>
/// </summary>
public static class NodeContentJson
{
    /// <summary>The JSON property carrying a content's type discriminator.</summary>
    public const string TypeProperty = "$type";

    /// <summary>
    /// Content built from <paramref name="existingContent"/> with <paramref name="updates"/>
    /// applied over it, carrying a <c>$type</c> discriminator. Read-merge-write: every key the
    /// caller does not name survives untouched.
    ///
    /// <para>The discriminator is the one the existing content already carries — the stored value
    /// is authoritative, because only the mesh knows what type this node really is — falling back
    /// to <paramref name="fallbackType"/> when the content carries none (a fresh node, or one
    /// already damaged by the defect above). A null/blank fallback on type-less content simply
    /// leaves the discriminator out rather than inventing one.</para>
    ///
    /// <para>A null value in <paramref name="updates"/> WRITES a JSON null — that is how a field
    /// is cleared. Callers that mean "leave it alone" must not include the key.</para>
    /// </summary>
    /// <param name="existingContent">The content as stored, in whatever shape it arrived.</param>
    /// <param name="fallbackType">Discriminator to use when the existing content carries none.</param>
    /// <param name="updates">The keys to set, in the camelCase spelling the mesh stores.</param>
    public static JsonObject Merge(
        object? existingContent,
        string? fallbackType,
        IEnumerable<KeyValuePair<string, object?>> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var merged = ToJsonObject(existingContent);
        var type = TypeOf(merged) ?? fallbackType;
        if (!string.IsNullOrWhiteSpace(type))
            merged[TypeProperty] = JsonValue.Create(type);
        foreach (var (key, value) in updates)
            merged[key] = ToNode(value);
        return merged;
    }

    /// <summary>
    /// Fresh content of type <paramref name="type"/> carrying <paramref name="values"/> — the
    /// create counterpart of <see cref="Merge"/>, for a node this module authors outright.
    /// </summary>
    /// <param name="type">The content's <c>$type</c> discriminator.</param>
    /// <param name="values">The content's fields, in the camelCase spelling the mesh stores.</param>
    public static JsonObject Create(string type, IEnumerable<KeyValuePair<string, object?>> values)
        => Merge(existingContent: null, fallbackType: type, updates: values);

    /// <summary>
    /// <paramref name="content"/> as a mutable JSON object, whatever shape it arrived in — a
    /// typed record (the owning hub resolved its <c>$type</c>), a <see cref="JsonElement"/> (a hub
    /// that could not), a <see cref="JsonNode"/> (the as-written DOM), or a dictionary. Unreadable
    /// or absent content yields an EMPTY object rather than throwing: a login callback must not
    /// fail because a profile's content is malformed.
    ///
    /// <para>A typed record is round-tripped through camelCase JSON — the casing the mesh stores
    /// and every reader expects.</para>
    /// </summary>
    /// <param name="content">The content to read.</param>
    public static JsonObject ToJsonObject(object? content)
    {
        switch (content)
        {
            case null:
                return new JsonObject();

            case JsonObject obj:
                // Deep clone: the caller's object must not be mutated by our writes, and a node
                // still attached to a parent cannot be re-parented.
                return obj.DeepClone().AsObject();

            case JsonNode node:
                return node.GetValueKind() == JsonValueKind.Object
                    ? node.DeepClone().AsObject()
                    : new JsonObject();

            case JsonElement { ValueKind: JsonValueKind.Object } element:
                return JsonObject.Create(element) ?? new JsonObject();

            case JsonElement:
                return new JsonObject();

            case IDictionary<string, object?> dict:
                {
                    var result = new JsonObject();
                    foreach (var (key, value) in dict)
                        result[key] = ToNode(value);
                    return result;
                }

            default:
                try
                {
                    return JsonSerializer.SerializeToNode(content, content.GetType(), CamelCase)
                        is JsonObject serialized
                        ? serialized
                        : new JsonObject();
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    // Unserializable content must not break the caller — it gets a valid object
                    // carrying whatever the caller is about to write.
                    return new JsonObject();
                }
        }
    }

    /// <summary>
    /// The <c>$type</c> discriminator <paramref name="content"/> carries, or null when it carries
    /// none — or carries one this module wrote as a Dictionary before issue #52 was fixed, which
    /// names no mesh type and must therefore never be preserved as if it did.
    /// </summary>
    /// <param name="content">The content to inspect, in any shape.</param>
    public static string? TypeOf(object? content)
    {
        var value = ToJsonObject(content).TryGetPropertyValue(TypeProperty, out var node)
            && node?.GetValueKind() == JsonValueKind.String
                ? node.GetValue<string>()
                : null;
        return string.IsNullOrWhiteSpace(value) || IsClrCollectionName(value!) ? null : value;
    }

    /// <summary>
    /// Whether <paramref name="type"/> is a CLR COLLECTION name the platform's type registry
    /// generated (<c>Dictionary`2[String,Object]</c>, <c>List`1[…]</c>) rather than a mesh content
    /// type. Such a value is the FINGERPRINT of the issue-#52 corruption, so re-merging over a
    /// damaged node must discard it and fall back to the real type instead of carefully preserving
    /// the damage.
    /// </summary>
    /// <param name="type">The discriminator to classify.</param>
    public static bool IsClrCollectionName(string type) =>
        type.Contains('`', StringComparison.Ordinal);

    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>A CLR value as a JSON node, preserving whatever JSON shape it already had.</summary>
    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        DateTimeOffset offset => JsonValue.Create(offset.UtcDateTime),
        DateTime time => JsonValue.Create(AsUtc(time)),
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), CamelCase),
    };

    /// <summary>
    /// A timestamp as the UTC instant the mesh stores.
    ///
    /// <para>🚨 <b>Every stored timestamp in the mesh is UTC, and writing an OFFSET instead of
    /// <c>Z</c> silently moves it.</b> System.Text.Json writes a <see cref="DateTimeOffset"/> as
    /// <c>2026-08-23T13:48:00+00:00</c>, and reading THAT back into a <see cref="DateTime"/>
    /// property — which is what every content record here declares — yields
    /// <see cref="DateTimeKind.Local"/>, i.e. the same instant re-expressed in the SERVER's zone.
    /// The value then compares unequal to the UTC one it was written from and renders as the wrong
    /// wall-clock time wherever it is formatted (the post page labels these "UTC"). Writing the
    /// instant with a <c>Z</c> keeps the read Kind <c>Utc</c> and the number the same. Caught by
    /// <c>PublishProblem_SurvivesTheTypedRoundTrip</c>, on a container that happened not to be
    /// running in UTC — the only reason it was visible at all.</para>
    ///
    /// <para>An UNSPECIFIED kind is STAMPED as UTC, never converted: the mesh's convention is that
    /// a bare timestamp already means UTC (see <c>ScheduledPostWatcher.SlotOf</c>, which parses one
    /// with <c>AssumeUniversal</c>), so converting it would move a slot by the server's offset —
    /// exactly the error this method exists to prevent, in the other direction.</para>
    /// </summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
