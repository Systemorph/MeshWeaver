using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.More;

namespace OpenSmc.Messaging.Serialization;

public class ImmutableDictionaryOfStringObjectConverter : JsonConverter<ImmutableDictionary<string, object>>
{
    public override ImmutableDictionary<string, object> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var node = doc.RootElement.AsNode();
        return Deserialize(node, options);
    }

    public override void Write(Utf8JsonWriter writer, ImmutableDictionary<string, object> value, JsonSerializerOptions options)
        => Serialize(value, options).WriteTo(writer);

    private static JsonObject Serialize(ImmutableDictionary<string, object> value, JsonSerializerOptions options)
    {
        var ret = new JsonObject(
            value.ToDictionary(
                x => x.Key,
                x => JsonSerializer.SerializeToNode(x.Value, x.Value.GetType(), options)
            ));
        return ret;
    }

    private static ImmutableDictionary<string, object> Deserialize(JsonNode serializedDictionary, JsonSerializerOptions options)
    {
        if (serializedDictionary is not JsonObject obj)
            throw new JsonException("Expected JsonObject as a source for dictionary.");

        var ret = obj
            .Select(kvp => new KeyValuePair<string, object>(kvp.Key, kvp.Value.Deserialize<object>(options)))
            .ToImmutableDictionary();
        return ret;
    }
}
