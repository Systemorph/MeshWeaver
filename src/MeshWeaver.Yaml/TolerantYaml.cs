using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace MeshWeaver.Yaml;

/// <summary>
/// 🚨 <b>YAML syntax, JSON semantics.</b> The platform's YAML reader and writer: text is parsed
/// into a <see cref="JsonNode"/> tree and bound with System.Text.Json under the same tolerant rules
/// the platform applies to its JSON node files — property names matched case-insensitively,
/// unknown keys ignored, numbers read from strings, enums as strings — plus two YAML-specific
/// tolerances: an unquoted number or boolean binds to a <see cref="string"/> property as the text
/// it was written as (<c>00123</c> as <c>"00123"</c>, <c>TRUE</c> as <c>"TRUE"</c> — the tree keeps
/// the core-schema value, <see cref="Deserialize{T}"/> restores the spelling where a string is
/// declared), and a quoted <c>"true"</c>/<c>"false"</c> binds to a <see cref="bool"/>.
///
/// <para>The full semantics are in <c>Doc/Architecture/TolerantYaml</c>.</para>
/// </summary>
public static class TolerantYaml
{
    /// <summary>
    /// The default binding options, built once and frozen (<see cref="JsonSerializerOptions.MakeReadOnly()"/>):
    /// an immutable value, never state.
    /// </summary>
    private static readonly JsonSerializerOptions DefaultOptions = Frozen(CreateSerializerOptions());

    private static JsonSerializerOptions Frozen(JsonSerializerOptions options)
    {
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// A NEW instance of the tolerant binding options (callers may extend it before first use):
    /// case-insensitive property names written camelCase, unknown members skipped, numbers
    /// readable from strings (and the named floating-point literals), enums as strings, nulls
    /// omitted on write, plus the tolerant string and boolean converters.
    /// </summary>
    public static JsonSerializerOptions CreateSerializerOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(),
            new TolerantStringConverter(),
            new TolerantBooleanConverter(),
        },
    };

    /// <summary>
    /// Parses the FIRST document of <paramref name="yaml"/> into a JSON tree: a mapping becomes a
    /// <see cref="JsonObject"/>, a sequence a <see cref="JsonArray"/>, a scalar a
    /// <see cref="JsonValue"/> (or <c>null</c>). Returns <c>null</c> for an empty text.
    /// </summary>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="options">How strictly to read; tolerant when omitted.</param>
    /// <exception cref="YamlParseException">The text cannot be read.</exception>
    public static JsonNode? Parse(string yaml, YamlParseOptions? options = null)
    {
        var documents = ParseAll(yaml, options);
        return documents.Count == 0 ? null : documents[0];
    }

    /// <summary>Parses every document of a multi-document text (<c>---</c> separated).</summary>
    /// <param name="yaml">The YAML text.</param>
    /// <param name="options">How strictly to read; tolerant when omitted.</param>
    /// <exception cref="YamlParseException">The text cannot be read.</exception>
    public static IReadOnlyList<JsonNode?> ParseAll(string yaml, YamlParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return YamlParser.ParseStream(yaml, (options ?? YamlParseOptions.Tolerant).Strict);
    }

    /// <summary>
    /// Parses <paramref name="yaml"/> and binds it to <typeparamref name="T"/> with
    /// <paramref name="serializerOptions"/> — the tolerant set (<see cref="CreateSerializerOptions"/>)
    /// when omitted. An empty document yields <c>default</c>.
    ///
    /// <para>A PLAIN scalar the core schema resolves to a number or boolean binds into a
    /// <see cref="string"/> as its source text (<c>00123</c> → <c>"00123"</c>, <c>0x1F</c> →
    /// <c>"0x1F"</c>, <c>TRUE</c> → <c>"TRUE"</c>), whenever the options carry the tolerant string
    /// converter; typed targets bind the resolved value (#7398).</para>
    /// </summary>
    /// <exception cref="YamlParseException">The text cannot be read.</exception>
    /// <exception cref="JsonException">The tree does not bind to <typeparamref name="T"/>.</exception>
    public static T? Deserialize<T>(string yaml, JsonSerializerOptions? serializerOptions = null, YamlParseOptions? parseOptions = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var (documents, spellings) = YamlParser.ParseStreamWithSpellings(yaml, (parseOptions ?? YamlParseOptions.Tolerant).Strict);
        var node = documents.Count == 0 ? null : documents[0];
        if (node is null)
            return default;
        var options = serializerOptions ?? DefaultOptions;
        if (!spellings.IsEmpty)
        {
            // System.Text.Json locks the options on first use anyway; locking here lets the
            // contract be read before that first use.
            if (!options.IsReadOnly)
                options.MakeReadOnly(populateMissingResolver: true);
            // TryGetTypeInfo, never GetConverter: options whose resolver declares no string contract (a
            // source-generated context for a numeric-only target) have no string to restore and must
            // bind exactly as before, not throw NotSupportedException for the missing metadata.
            if (options.TryGetTypeInfo(typeof(string), out var stringContract) && stringContract.Converter is TolerantStringConverter)
                node = SourceSpelling.Restore(node, options.GetTypeInfo(typeof(T)), spellings);
        }
        return node.Deserialize<T>(options);
    }

    /// <summary>Writes <paramref name="node"/> as block-style YAML (no document markers).</summary>
    public static string Serialize(JsonNode? node) => YamlEmitter.Emit(node);

    /// <summary>
    /// Serializes <paramref name="value"/> to a JSON tree with <paramref name="serializerOptions"/>
    /// (the tolerant set when omitted — camelCase names, nulls omitted) and writes it as YAML.
    /// </summary>
    public static string Serialize<T>(T value, JsonSerializerOptions? serializerOptions = null) =>
        YamlEmitter.Emit(JsonSerializer.SerializeToNode(value, serializerOptions ?? DefaultOptions));

    /// <summary>
    /// A scalar node's text as the core schema RESOLVED it — a string's value, <c>true</c>/<c>false</c>,
    /// a number's digits (<c>007</c> reads <c>7</c>, <c>TRUE</c> reads <c>true</c>; float digits such as
    /// <c>1.10</c> are kept) — or <c>null</c> for a null node. A collection yields its JSON text. The
    /// source spelling is not on the tree; <see cref="Deserialize{T}"/> restores it for a string target.
    /// </summary>
    public static string? ScalarText(JsonNode? node) => YamlScalars.Text(node);
}

/// <summary>
/// Binds a string property from ANY scalar: a YAML author writing <c>version: 1.10</c> or
/// <c>enabled: true</c> into a string field means the text, exactly as written. The converter sees
/// the RESOLVED token; for a plain scalar whose resolved form reads differently (<c>00123</c>,
/// <c>0x1F</c>, <c>TRUE</c>), <see cref="TolerantYaml.Deserialize{T}"/> has already put the source
/// spelling back as a string token (<see cref="SourceSpelling"/>).
/// </summary>
internal sealed class TolerantStringConverter : JsonConverter<string>
{
    public override bool HandleNull => false;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Cannot bind a {reader.TokenType} to a string."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);

    public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() ?? string.Empty;

    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value);
}

/// <summary>Binds a boolean from <c>true</c>/<c>false</c> as a boolean OR as a (case-insensitive) string.</summary>
internal sealed class TolerantBooleanConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String when bool.TryParse(reader.GetString(), out var b) => b,
            _ => throw new JsonException($"Cannot bind a {reader.TokenType} to a boolean."),
        };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);
}

/// <summary>
/// Puts a plain scalar's SOURCE spelling back wherever the bound type declares a
/// <see cref="string"/> (#7398): a property, a collection element, a dictionary value, at any depth.
/// The parse resolves <c>00123</c> to the number 123 because the tree is YAML 1.2 core schema; a
/// string-typed target means the text, so before binding the walk follows the target's
/// <see cref="JsonTypeInfo"/> alongside the tree and replaces exactly those scalars with the text
/// the parser recorded for them. Typed targets (<c>int</c>, <c>bool</c>, <c>object</c>,
/// <see cref="JsonElement"/>) and properties with their own converter keep the resolved node.
/// </summary>
internal static class SourceSpelling
{
    /// <summary>
    /// <paramref name="node"/> with every string-bound recorded scalar replaced by its spelling;
    /// the node itself when it is such a scalar is returned replaced, collections are updated in place.
    /// </summary>
    internal static JsonNode Restore(JsonNode node, JsonTypeInfo target, ImmutableDictionary<JsonNode, string> spellings)
    {
        if (target.Type == typeof(string))
            return node is JsonValue && spellings.TryGetValue(node, out var text) ? JsonValue.Create(text) : node;
        switch (target.Kind)
        {
            case JsonTypeInfoKind.Object when node is JsonObject map:
                {
                    var names = target.Options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                    foreach (var key in map.Select(entry => entry.Key).ToImmutableList())
                    {
                        var property = target.Properties.FirstOrDefault(p => !p.IsExtensionData && names.Equals(p.Name, key));
                        if (property is null || property.CustomConverter is not null || map[key] is not { } child)
                            continue;
                        var restored = Restore(child, target.Options.GetTypeInfo(property.PropertyType), spellings);
                        if (!ReferenceEquals(restored, child))
                            map[key] = restored;
                    }
                    return map;
                }
            case JsonTypeInfoKind.Enumerable when node is JsonArray seq && target.ElementType is { } elementType:
                {
                    var element = target.Options.GetTypeInfo(elementType);
                    for (var i = 0; i < seq.Count; i++)
                        if (seq[i] is { } child && Restore(child, element, spellings) is var restored && !ReferenceEquals(restored, child))
                            seq[i] = restored;
                    return seq;
                }
            case JsonTypeInfoKind.Dictionary when node is JsonObject map && target.ElementType is { } valueType:
                {
                    var value = target.Options.GetTypeInfo(valueType);
                    foreach (var key in map.Select(entry => entry.Key).ToImmutableList())
                        if (map[key] is { } child && Restore(child, value, spellings) is var restored && !ReferenceEquals(restored, child))
                            map[key] = restored;
                    return map;
                }
            default:
                return node;
        }
    }
}
