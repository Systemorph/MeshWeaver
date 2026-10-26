using System.Collections.Immutable;
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
/// <para>Bounded by construction: at most <see cref="MaxKeys"/> property names are listed, each
/// cut to <see cref="MaxNameChars"/> characters and JSON-escaped (a name cannot inject a line
/// break into the log), and measuring a <see cref="JsonElement"/> reads its length without
/// materialising the payload.</para>
/// </summary>
public static class ContentShapes
{
    /// <summary>The most top-level property names a description lists; the total is always given.</summary>
    public const int MaxKeys = 32;

    /// <summary>The longest property name (or <c>$type</c> value) a description prints before cutting it.</summary>
    public const int MaxNameChars = 64;

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
        var bytes = JsonMarshal.GetRawUtf8Value(element).Length;
        if (element.ValueKind != JsonValueKind.Object)
            return Compose(element.ValueKind, "no $type", null, 0, bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var names = ImmutableList.CreateBuilder<string>();
        var total = 0;
        var type = "no $type";
        foreach (var property in element.EnumerateObject())
        {
            total++;
            if (names.Count < MaxKeys)
                names.Add(property.Name);
            if (property.Name == TypeDiscriminator)
                type = property.Value.ValueKind == JsonValueKind.String
                    ? $"$type={Quote(property.Value.GetString() ?? string.Empty)}"
                    : $"$type is a JSON {property.Value.ValueKind}";
        }
        return Compose(JsonValueKind.Object, type, names.ToImmutable(), total,
            bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                names.Add(property.Key);
            if (property.Key == TypeDiscriminator)
                type = property.Value is JsonValue value && value.TryGetValue<string>(out var discriminator)
                    ? $"$type={Quote(discriminator)}"
                    : $"$type is a JSON {Kind(property.Value)}";
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

    private static string Compose(JsonValueKind kind, string type, ImmutableList<string>? names, int total, string bytes)
        => Compose(kind.ToString(), type, names, total, bytes);

    private static string Compose(
        string kind, string type, ImmutableList<string>? names, int total, string bytes)
    {
        var keys = names is null
            ? string.Empty
            : $", {total} top-level key{(total == 1 ? "" : "s")} ["
              + string.Join(", ", names.Select(Quote))
              + (total > names.Count ? $", … {total - names.Count} more" : string.Empty)
              + "]";
        return $"{type}, JSON {kind}{keys}, {bytes} bytes (values not logged)";
    }

    private static string Quote(string name)
        => JsonSerializer.Serialize(name.Length <= MaxNameChars ? name : name[..MaxNameChars] + "…");

    /// <summary>
    /// The approximate UTF-8 length a <see cref="JsonNode"/> serializes to, summed by walking the
    /// tree — never by serializing it: that would allocate the whole payload for a diagnostic, and
    /// a customised <see cref="JsonValue"/> would run an arbitrary converter. String escaping is not
    /// counted and a value of an unrecognised CLR type counts as zero, hence the <c>~</c>.
    /// </summary>
    private static string Measure(JsonNode node)
        => "~" + Size(node).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static long Size(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return 4; // null
            case JsonObject obj:
            {
                long total = 2 + Math.Max(0, obj.Count - 1);
                foreach (var property in obj)
                    total += Encoding.UTF8.GetByteCount(property.Key) + 3 + Size(property.Value);
                return total;
            }
            case JsonArray array:
            {
                long total = 2 + Math.Max(0, array.Count - 1);
                foreach (var item in array)
                    total += Size(item);
                return total;
            }
            case JsonValue value when value.TryGetValue<JsonElement>(out var element):
                return JsonMarshal.GetRawUtf8Value(element).Length;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return Encoding.UTF8.GetByteCount(text) + 2;
            case JsonValue value when value.TryGetValue<bool>(out var flag):
                return flag ? 4 : 5;
            case JsonValue value when value.TryGetValue<int>(out var small):
                return small.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
            case JsonValue value when value.TryGetValue<long>(out var integer):
                return integer.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
            case JsonValue value when value.TryGetValue<decimal>(out var number):
                return number.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
            case JsonValue value when value.TryGetValue<double>(out var real):
                return real.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;
            default:
                return 0;
        }
    }
}
