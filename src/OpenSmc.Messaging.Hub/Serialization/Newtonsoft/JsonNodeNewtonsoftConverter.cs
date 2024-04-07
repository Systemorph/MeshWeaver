using System.Text.Json.Nodes;
using Newtonsoft.Json;

namespace OpenSmc.Messaging.Serialization.Newtonsoft;

public class JsonNodeNewtonsoftConverter : JsonConverter
{
    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        throw new NotImplementedException();
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        var node = (JsonNode)value!;
        writer.WriteRawValue(node.ToJsonString());
    }

    public override bool CanConvert(Type objectType)
        => typeof(JsonNode).IsAssignableFrom(objectType);
}