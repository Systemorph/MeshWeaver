using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.More;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.Data.Serialization;

public class InstancesInCollectionConverter(ITypeRegistry typeRegistry) : JsonConverter<InstanceCollection>
{
    public const string CollectionProperty = "$collection";


    private JsonNode Serialize(InstanceCollection instances, JsonSerializerOptions options)
    {
        return new JsonObject(instances.Instances.Select(x =>
            new KeyValuePair<string, JsonNode>(JsonSerializer.Serialize(x.Key, options),
                JsonSerializer.SerializeToNode(x.Value, options))));
    }

    public override InstanceCollection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var obj = (JsonObject)doc.RootElement.AsNode();
        if (obj == null)
            return null;
        var collection = obj[CollectionProperty]?.ToString();
        var keyType = typeRegistry.GetKeyFunction(collection).KeyType;
        var type = keyType ?? typeof(object);
        return new InstanceCollection
        {
            Instances = obj
                .Where(i => i.Key != CollectionProperty)
                .Select(i =>
                    new KeyValuePair<object, object>(
                        JsonSerializer.Deserialize(i.Key, type, options),
                        i.Value.Deserialize<object>(options)
                    )
                )
                .ToImmutableDictionary()
        };
    }


    public override void Write(Utf8JsonWriter writer, InstanceCollection value, JsonSerializerOptions options)
    {
        Serialize(value, options).WriteTo(writer);
    }
}
