using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Messaging.Serialization;

public class RawJsonConverter() : JsonConverter<RawJson>
{
    public override RawJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // RawJson is an opaque, already-serialized blob — capture the raw text and DON'T inflate it.
        // The previous JsonNode.Parse(...).ToJsonString() built a full mutable DOM (one heap object per
        // array element / property, plus backing dictionaries) and then re-serialized it. On the hot
        // Orleans path every cross-grain RouteMessage deep-copies the whole IMessageDelivery via JsonCodec
        // (serialize -> deserialize), so a single large payload (e.g. a mesh-wide AI search result) produced
        // a Gen0 allocation storm that drove the runtime into a GC death-spiral and ultimately OutOfMemory
        // (the 2026-06-23 atioz restart loop). JsonDocument parses into pooled buffers and GetRawText copies
        // the original UTF-8 slice out in a SINGLE managed allocation — same Content, ~no transient garbage.
        using var doc = JsonDocument.ParseValue(ref reader);
        return new RawJson(doc.RootElement.GetRawText());
    }

    public override void Write(Utf8JsonWriter writer, RawJson value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value?.Content))
            writer.WriteNullValue();
        else
            writer.WriteRawValue(value.Content);
    }
}
