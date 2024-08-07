using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeshWeaver.Messaging.Serialization;

public class RawJsonConverter(ITypeRegistry typeRegistry) : JsonConverter<RawJson>
{
    public override RawJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader);
        var type = node["$type"];
        var content = type != null && typeRegistry.GetOrAddTypeName(typeof(RawJson)) == type.GetValue<string>()
            ? node["content"].GetValue<string>()
            : node.ToJsonString();
        return new RawJson(content);
    }

    public override void Write(Utf8JsonWriter writer, RawJson value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value?.Content))
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteRawValue(value.Content);
        }
    }
}
