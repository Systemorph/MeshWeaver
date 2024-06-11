using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenSmc.Messaging.Serialization;

public class RawJsonConverter : JsonConverter<RawJson>
{
    public override RawJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader);
        var content = node.ToJsonString();
        return new RawJson(content);
    }

    public override void Write(Utf8JsonWriter writer, RawJson value, JsonSerializerOptions options)
    {
        // TODO V10: we just do the base logic here keeping RawJson in the output, but we might think about unwrapping it. However this requires to change generic type on top from MessageDelivery<RawJson> to be MessageDelivery<> around particular type (2024/06/11, Dmitry Kalabin)
        var clonedOptions = CloneOptions(options);
        JsonSerializer.Serialize(writer, value, clonedOptions);
    }

    private JsonSerializerOptions CloneOptions(JsonSerializerOptions options)
    {
        var clonedOptions = new JsonSerializerOptions(options);
        clonedOptions.Converters.Remove(this);
        return clonedOptions;
    }
}
