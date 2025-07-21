using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeshWeaver.Messaging.Serialization;

public class RawJsonConverter() : JsonConverter<RawJson>
{
    public override RawJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader);
        return new RawJson(node?.ToJsonString() ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, RawJson value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value?.Content))
            writer.WriteNullValue();
        else
            writer.WriteRawValue(value.Content);
    }
}
