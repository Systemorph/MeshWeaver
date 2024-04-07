using Json.Patch;
using Newtonsoft.Json;
using OpenSmc.Data;
using OpenSmc.Serialization;

namespace OpenSmc.Messaging.Serialization;

public class DataChangedConverter(ISerializationService serializationService) : JsonConverter<DataChangedEvent>
{
    public override void WriteJson(JsonWriter writer, JsonPatch value, JsonSerializer serializer)
    {
        writer.WriteRawValue(System.Text.Json.JsonSerializer.Serialize(value));
    }

    public override JsonPatch ReadJson(JsonReader reader, Type objectType, JsonPatch existingValue, bool hasExistingValue,
        JsonSerializer serializer)
    {
        return System.Text.Json.JsonSerializer.Deserialize<JsonPatch>(reader.ReadAsString()!);
    }
}