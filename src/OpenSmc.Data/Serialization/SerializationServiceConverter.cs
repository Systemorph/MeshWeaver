using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenSmc.Serialization;

namespace OpenSmc.Data.Serialization;

public class SerializationServiceConverter(ISerializationService serializationService) : JsonConverter<object>
{

    public override bool CanConvert(Type typeToConvert)
        => typeToConvert != typeof(EntityStore);

    public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        return serializationService.Deserialize(doc.RootElement.ToString());
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        JsonNode.Parse(serializationService.SerializeToString(value))!.WriteTo(writer);
    }
}

