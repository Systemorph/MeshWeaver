using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.More;

namespace OpenSmc.Data.Serialization;

public class InstancesInCollectionConverter() : JsonConverter<InstanceCollection>
{

    private InstanceCollection Deserialize(JsonNode node, JsonSerializerOptions options)
    {
        if (node is not JsonObject obj)
            throw new ArgumentException("Expecting an array");
        return new(){Instances = obj.Select(jsonNode => DeserializeEntity(jsonNode.Key, jsonNode.Value, options)).ToImmutableDictionary()};
    }

    private KeyValuePair<object, object> DeserializeEntity(string idSerialized, JsonNode jsonNode, JsonSerializerOptions options)
    {
        return new(
            JsonSerializer.Deserialize<object>(idSerialized, options),
            jsonNode.Deserialize<object>(options));
    }

    private JsonNode Serialize(InstanceCollection instances, JsonSerializerOptions options)
    {
        return new JsonObject(instances.Instances.Select(y =>
            new KeyValuePair<string, JsonNode>(JsonSerializer.Serialize(y.Key, options),
                JsonNode.Parse(JsonSerializer.Serialize(y.Value, options))))
        );
    }

    public override InstanceCollection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        return Deserialize(doc.RootElement.AsNode(), options);
    }


    public override void Write(Utf8JsonWriter writer, InstanceCollection value, JsonSerializerOptions options)
    {
        Serialize(value, options).WriteTo(writer);
    }
}