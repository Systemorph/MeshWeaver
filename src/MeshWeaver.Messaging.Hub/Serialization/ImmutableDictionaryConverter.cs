using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.More;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// JSON converter for <see cref="ImmutableDictionary{TKey, TValue}"/> keyed by string with
/// arbitrary object values, mapping a JSON object to/from the immutable dictionary and
/// deserializing each value loosely as <see cref="object"/>.
/// </summary>
public class ImmutableDictionaryOfStringObjectConverter : JsonConverter<ImmutableDictionary<string, object?>>
{
    /// <summary>
    /// Reads a JSON object into an immutable string-to-object dictionary; an empty/null
    /// payload yields an empty dictionary.
    /// </summary>
    /// <param name="reader">The reader positioned at the dictionary value.</param>
    /// <param name="typeToConvert">The target dictionary type.</param>
    /// <param name="options">The active serializer options used to deserialize values.</param>
    /// <returns>The materialized immutable dictionary.</returns>
    public override ImmutableDictionary<string, object?> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var node = doc.RootElement.AsNode();
        return node is null ? ImmutableDictionary<string, object?>.Empty : Deserialize(node, options);
    }

    /// <summary>
    /// Writes the immutable dictionary as a JSON object, serializing each value by its
    /// runtime type (null values are emitted as JSON null).
    /// </summary>
    /// <param name="writer">The writer to emit the dictionary to.</param>
    /// <param name="value">The dictionary to serialize.</param>
    /// <param name="options">The active serializer options used to serialize values.</param>
    public override void Write(Utf8JsonWriter writer, ImmutableDictionary<string, object?> value, JsonSerializerOptions options)
        => Serialize(value, options).WriteTo(writer);

    private static JsonObject Serialize(ImmutableDictionary<string, object?> value, JsonSerializerOptions options)
    {
        var ret = new JsonObject(
            value.ToDictionary(
                x => x.Key,
                x => x.Value is null ? null : JsonSerializer.SerializeToNode(x.Value, x.Value.GetType(), options)
            ));
        return ret;
    }

    private static ImmutableDictionary<string, object?> Deserialize(JsonNode serializedDictionary, JsonSerializerOptions options)
    {
        if (serializedDictionary is not JsonObject obj)
            throw new JsonException("Expected JsonObject as a source for dictionary.");

        var ret = obj
            .Select(kvp => new KeyValuePair<string, object?>(kvp.Key, kvp.Value?.Deserialize<object>(options)))
            .ToImmutableDictionary();
        return ret;
    }
}
