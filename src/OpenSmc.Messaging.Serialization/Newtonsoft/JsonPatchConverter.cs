using System.Text.Json;
using System.Text.Json.Serialization;
using Json.More;
using Json.Patch;

namespace OpenSmc.Messaging.Serialization.Newtonsoft;

public class JsonPatchConverter : JsonConverter<JsonPatch>
{
    private const string Patch = nameof(Patch);
    public override JsonPatch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.AsNode().Deserialize<JsonPatch>();
    }

    public override void Write(Utf8JsonWriter writer, JsonPatch value, JsonSerializerOptions options)
    {
        JsonSerializer.SerializeToNode(value)!.WriteTo(writer);
    }
}