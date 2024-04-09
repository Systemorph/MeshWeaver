using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.More;

namespace OpenSmc.Data.Serialization;

public class InstancesInCollectionConverter : JsonConverter<InstanceCollection>
{
    private const string IdName = "$id";


    private JsonNode Serialize(InstanceCollection instances, JsonSerializerOptions options)
    {
        return new JsonObject(instances.Instances.Select(x =>
            new KeyValuePair<string, JsonNode>(JsonSerializer.Serialize(x.Key, options),
                JsonSerializer.SerializeToNode(x.Value, options))));
    }

    public override InstanceCollection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        var obj = (JsonObject)doc.RootElement.AsNode();
        if (obj == null)
            return null;
        return new InstanceCollection
        {
            Instances = obj.Select(i =>
                    new KeyValuePair<object, object>(
                        JsonSerializer.Deserialize<object>(i.Key, options),
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