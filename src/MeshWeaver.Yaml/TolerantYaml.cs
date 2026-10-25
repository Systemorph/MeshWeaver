using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeshWeaver.Yaml;

/// <summary>
/// 🚨 <b>YAML syntax, JSON semantics.</b> The platform's YAML reader and writer: text is parsed
/// into a <see cref="JsonNode"/> tree and bound with System.Text.Json under the same tolerant rules
/// the platform applies to its JSON node files — property names matched case-insensitively,
/// unknown keys ignored, numbers read from strings, enums as strings — plus two YAML-specific
/// tolerances: an unquoted number or boolean binds to a <see cref="string"/> property as the text
/// it was written as, and a quoted <c>"true"</c>/<c>"false"</c> binds to a <see cref="bool"/>.
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
    /// </summary>
    /// <exception cref="YamlParseException">The text cannot be read.</exception>
    /// <exception cref="JsonException">The tree does not bind to <typeparamref name="T"/>.</exception>
    public static T? Deserialize<T>(string yaml, JsonSerializerOptions? serializerOptions = null, YamlParseOptions? parseOptions = null)
    {
        var node = Parse(yaml, parseOptions);
        return node is null ? default : node.Deserialize<T>(serializerOptions ?? DefaultOptions);
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
    /// A scalar node's text as YAML wrote it — a string's value, <c>true</c>/<c>false</c>, a
    /// number's digits — or <c>null</c> for a null node. A collection yields its JSON text.
    /// </summary>
    public static string? ScalarText(JsonNode? node) => YamlScalars.Text(node);
}

/// <summary>
/// Binds a string property from ANY scalar: a YAML author writing <c>version: 1.10</c> or
/// <c>enabled: true</c> into a string field means the text, exactly as written.
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
