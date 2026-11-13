using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshWeaver.Mesh;

/// <summary>
/// 🚨 <b>The SHAPE of a node's content, for a diagnostic — never its values.</b>
///
/// <para>A content degradation (<see cref="MeshNodeContentDegradedException"/>) and its siblings
/// used to append the stored JSON itself to the log line. Those lines are Warning and above, so
/// they ship to the central log store, where row-level security does not reach: a node only its
/// owner may read had its content copied to every reader of the logs. What a reader needs to
/// diagnose a degraded type is the shape — which <c>$type</c> the row claims (or that it claims
/// none), which top-level properties it carries (a missing required one is the usual cause), what
/// kind of JSON value it is and how big — and none of that needs a value.</para>
///
/// <para><b>Bounded by construction</b>, in output AND in allocation, because a degradation is
/// exactly when the payload is unusual:</para>
/// <list type="bullet">
/// <item>at most <see cref="MaxKeys"/> property names are listed (the total is always given), and
/// only those are decoded — the discriminator is matched on raw bytes
/// (<see cref="JsonProperty.NameEquals(string)"/>), so a huge object costs no per-key string;</item>
/// <item>a displayed name or <c>$type</c> is cut to <see cref="MaxNameChars"/> BEFORE it is
/// decoded, and printed JSON-escaped (a name cannot inject a line break into the log);</item>
/// <item>a <see cref="JsonElement"/>'s length is read from its raw UTF-8 span;</item>
/// <item>a <see cref="JsonNode"/> is sized over its top level only (at most
/// <see cref="MaxSizedEntries"/> entries, numbers formatted into a stack buffer) and nested
/// containers are never descended into — descending would materialise a parsed tree's lazy
/// containers, an allocation that grows with the payload. Such a size is printed as a lower
/// bound (<c>≥</c>); a fully sized flat node as approximate (<c>~</c>, escaping not counted).</item>
/// </list>
/// </summary>
public static class ContentShapes
{
    /// <summary>The most top-level property names a description lists; the total is always given.</summary>
    public const int MaxKeys = 32;

    /// <summary>The longest property name (or <c>$type</c> value) a description prints before cutting it.</summary>
    public const int MaxNameChars = 64;

    /// <summary>The most top-level entries a <see cref="JsonNode"/> size is summed over; beyond it the size is a lower bound.</summary>
    public const int MaxSizedEntries = 1024;

    private const string TypeDiscriminator = "$type";

    /// <summary>
    /// Describes <paramref name="content"/> as <c>$type</c> (or its absence), value kind, top-level
    /// property names and byte length. No property VALUE ever appears in the result — only the
    /// <c>$type</c> discriminator, which names a type and is the key a reader needs.
    /// </summary>
    /// <param name="content">The content as the read seam holds it — normally a <see cref="JsonElement"/> or <see cref="JsonNode"/>.</param>
    /// <returns>A single-line, bounded description.</returns>
    public static string Describe(object? content)
    {
        try
        {
            return content switch
            {
                null => "no content (null)",
                JsonElement element => Element(element),
                JsonNode node => Node(node),
                string text => $"CLR string, {text.Length} chars (value not logged)",
                _ => $"CLR value of type '{content.GetType().FullName}' (value not logged)",
            };
        }
        catch (ObjectDisposedException)
        {
            // The read fault the caller is reporting stays on its warning; a disposed DOM has no shape to give.
            return "<disposed JSON>";
        }
    }

    private static string Element(JsonElement element)
    {
        var bytes = JsonMarshal.GetRawUtf8Value(element).Length.ToString(CultureInfo.InvariantCulture);
        if (element.ValueKind != JsonValueKind.Object)
            return Compose(element.ValueKind.ToString(), "no $type", null, 0, bytes);

        var names = ImmutableList.CreateBuilder<string>();
        var total = 0;
        // The LAST $type wins (as for a reader), and it is formatted once, after the walk: a
        // JsonElement keeps duplicate names, so formatting each occurrence would allocate per duplicate.
        JsonElement? discriminator = null;
        foreach (var property in element.EnumerateObject())
        {
            total++;
            if (names.Count < MaxKeys)
                names.Add(DisplayEscaped(JsonMarshal.GetRawUtf8PropertyName(property)));
            if (property.NameEquals(TypeDiscriminator))
                discriminator = property.Value;
        }
        var type = discriminator is { } found ? TypeOf(found) : "no $type";
        return Compose(nameof(JsonValueKind.Object), type, names.ToImmutable(), total, bytes);
    }

    /// <summary>The <c>$type</c> clause for a discriminator held as a <see cref="JsonElement"/>, decoded only up to the cut.</summary>
    private static string TypeOf(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return $"$type is a JSON {value.ValueKind}";
        var raw = JsonMarshal.GetRawUtf8Value(value); // includes the surrounding quotes
        return "$type=" + DisplayEscaped(raw[1..^1]);
    }

    private static string Node(JsonNode node)
    {
        var bytes = Measure(node);
        if (node is not JsonObject obj)
            return Compose(Kind(node), "no $type", null, 0, bytes);

        var names = ImmutableList.CreateBuilder<string>();
        var type = "no $type";
        foreach (var property in obj)
        {
            if (names.Count < MaxKeys)
                names.Add(DisplayUnescaped(property.Key));
            if (property.Key == TypeDiscriminator)
                type = property.Value switch
                {
                    JsonValue value when value.TryGetValue<JsonElement>(out var element) => TypeOf(element),
                    // A constructed string value: the string already exists, nothing is decoded.
                    JsonValue value when value.TryGetValue<string>(out var text) => "$type=" + DisplayUnescaped(text),
                    var other => $"$type is a JSON {Kind(other)}",
                };
        }
        return Compose(nameof(JsonValueKind.Object), type, names.ToImmutable(), obj.Count, bytes);
    }

    /// <summary>
    /// The JSON kind of a node, read without <see cref="JsonNode.GetValueKind"/>, which for a
    /// customised <see cref="JsonValue"/> may run its converter.
    /// </summary>
    private static string Kind(JsonNode? node) => node switch
    {
        null => nameof(JsonValueKind.Null),
        JsonObject => nameof(JsonValueKind.Object),
        JsonArray => nameof(JsonValueKind.Array),
        JsonValue value when value.TryGetValue<JsonElement>(out var element) => element.ValueKind.ToString(),
        JsonValue value when value.TryGetValue<string>(out _) => nameof(JsonValueKind.String),
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? nameof(JsonValueKind.True) : nameof(JsonValueKind.False),
        JsonValue value when value.TryGetValue<double>(out _) || value.TryGetValue<decimal>(out _)
                             || value.TryGetValue<long>(out _) || value.TryGetValue<int>(out _)
            => nameof(JsonValueKind.Number),
        _ => "value of an unrecognised CLR type",
    };

    private static string Compose(
        string kind, string type, ImmutableList<string>? names, int total, string bytes)
    {
        var keys = names is null
            ? string.Empty
            : $", {total} top-level key{(total == 1 ? "" : "s")} ["
              + string.Join(", ", names)
              + (total > names.Count ? $", … {total - names.Count} more" : string.Empty)
              + "]";
        return $"{type}, JSON {kind}{keys}, {bytes} bytes (values not logged)";
    }

    /// <summary>
    /// Displays a name that is ALREADY JSON-escaped (raw bytes from the document): cut to
    /// <see cref="MaxNameChars"/> bytes, never splitting a UTF-8 character, then decoded — only the
    /// cut prefix is ever turned into a string. Escaped JSON contains no raw control character, so
    /// the result cannot break the log line.
    /// </summary>
    private static string DisplayEscaped(ReadOnlySpan<byte> escaped)
    {
        var count = Math.Min(escaped.Length, MaxNameChars);
        while (count > 0 && count < escaped.Length && (escaped[count] & 0xc0) == 0x80)
            count--;
        return "\"" + Encoding.UTF8.GetString(escaped[..count]) + (count < escaped.Length ? "…" : "") + "\"";
    }

    /// <summary>Displays an unescaped name that already exists as a string: cut first, then JSON-escaped.</summary>
    private static string DisplayUnescaped(string name)
    {
        var cut = name.Length > MaxNameChars;
        var escaped = JsonSerializer.Serialize(cut ? name[..MaxNameChars] : name);
        return "\"" + escaped[1..^1] + (cut ? "…" : "") + "\"";
    }

    /// <summary>
    /// The approximate UTF-8 length of a <see cref="JsonNode"/>, summed over its TOP LEVEL — never
    /// by serializing it (that allocates the whole payload and runs a customised value's converter)
    /// and never by descending into a nested container (that materialises a parsed tree's lazy
    /// containers). A nested container, an unrecognised value, or more than
    /// <see cref="MaxSizedEntries"/> entries makes it a lower bound (<c>≥</c>); otherwise it is
    /// approximate (<c>~</c>), since string escaping is not counted.
    /// </summary>
    private static string Measure(JsonNode node)
    {
        var partial = false;
        long total;
        switch (node)
        {
            case JsonObject obj:
            {
                total = 2 + Math.Max(0, obj.Count - 1);
                var seen = 0;
                foreach (var property in obj)
                {
                    if (++seen > MaxSizedEntries) { partial = true; break; }
                    total += Encoding.UTF8.GetByteCount(property.Key) + 3 + LeafSize(property.Value, ref partial);
                }
                break;
            }
            case JsonArray array:
            {
                total = 2 + Math.Max(0, array.Count - 1);
                var seen = 0;
                foreach (var item in array)
                {
                    if (++seen > MaxSizedEntries) { partial = true; break; }
                    total += LeafSize(item, ref partial);
                }
                break;
            }
            default:
                total = LeafSize(node, ref partial);
                break;
        }
        return (partial ? "≥" : "~") + total.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The size of a value without descending into it or allocating for it.</summary>
    private static long LeafSize(JsonNode? node, ref bool partial)
    {
        Span<char> digits = stackalloc char[64];
        switch (node)
        {
            case null:
                return 4; // null
            case JsonObject or JsonArray:
                partial = true;
                return 2; // "{}" / "[]" at least
            case JsonValue value when value.TryGetValue<JsonElement>(out var element):
                return JsonMarshal.GetRawUtf8Value(element).Length;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return Encoding.UTF8.GetByteCount(text) + 2;
            case JsonValue value when value.TryGetValue<bool>(out var flag):
                return flag ? 4 : 5;
            case JsonValue value when value.TryGetValue<int>(out var small)
                                      && small.TryFormat(digits, out var written, default, CultureInfo.InvariantCulture):
                return written;
            case JsonValue value when value.TryGetValue<long>(out var integer)
                                      && integer.TryFormat(digits, out var written, default, CultureInfo.InvariantCulture):
                return written;
            case JsonValue value when value.TryGetValue<decimal>(out var number)
                                      && number.TryFormat(digits, out var written, default, CultureInfo.InvariantCulture):
                return written;
            case JsonValue value when value.TryGetValue<double>(out var real)
                                      && real.TryFormat(digits, out var written, "R", CultureInfo.InvariantCulture):
                return written;
            default:
                partial = true;
                return 0;
        }
    }
}
