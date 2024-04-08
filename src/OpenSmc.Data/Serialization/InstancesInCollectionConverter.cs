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


        return new JsonArray(instances.Instances.Values.Zip(
            instances.Instances.Keys.Select(k => JsonSerializer.SerializeToNode(k, options)),
            (o, k) =>
            {
                var obj = JsonSerializer.SerializeToNode(o, options)!;
                obj[IdName] = k;
                return obj;
            }).ToArray());

    }

    public override InstanceCollection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        var array = (JsonArray)doc.RootElement.AsNode();
        if (array == null)
            return null;
        return new InstanceCollection()
        {
            Instances = array.Select(i =>
                    new KeyValuePair<object, object>(
                        (i as JsonObject)?[IdName].Deserialize<object>(options),
                        i.Deserialize<object>(options)
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