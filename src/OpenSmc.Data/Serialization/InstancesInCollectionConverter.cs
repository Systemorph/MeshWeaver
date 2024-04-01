using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.More;
using OpenSmc.Serialization;

namespace OpenSmc.Data.Serialization;

public class InstancesInCollectionConverter(ISerializationService serializationService) : JsonConverter<InstanceCollection>
{

    private InstanceCollection Deserialize(JsonNode node)
    {
        if (node is not JsonObject obj)
            throw new ArgumentException("Expecting an array");
        return new(obj.Select(jsonNode => DeserializeEntity(jsonNode.Key, jsonNode.Value)).ToImmutableDictionary());
    }

    private KeyValuePair<object, object> DeserializeEntity(string idSerialized, JsonNode jsonNode)
    {
        return new(
            serializationService.Deserialize(idSerialized),
            serializationService.Deserialize(jsonNode.ToJsonString()));
    }

    private JsonNode Serialize(InstanceCollection instances)
    {
        return new JsonObject(instances.Instances.Select(y =>
            new KeyValuePair<string, JsonNode>(serializationService.SerializeToString(y.Key),
                JsonNode.Parse(serializationService.SerializeToString(y.Value))))
        );
    }

    public override InstanceCollection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        return Deserialize(doc.RootElement.AsNode());
    }


    public override void Write(Utf8JsonWriter writer, InstanceCollection value, JsonSerializerOptions options)
    {
        Serialize(value).WriteTo(writer);
    }
}