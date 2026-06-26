using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// Converter for <see cref="RawJson"/>, an opaque already-serialized JSON blob. It captures the raw
/// UTF-8 text on read and emits it verbatim on write, avoiding building an intermediate mutable DOM
/// — critical on the hot Orleans message-copy path to prevent allocation storms.
/// </summary>
public class RawJsonConverter() : JsonConverter<RawJson>
{
    /// <summary>
    /// Reads a value into a <see cref="RawJson"/> by copying the original JSON slice as raw text,
    /// without inflating it into a node tree.
    /// </summary>
    /// <param name="reader">The reader positioned at the value to capture.</param>
    /// <param name="typeToConvert">The target type (<see cref="RawJson"/>).</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>A <see cref="RawJson"/> wrapping the captured raw JSON text.</returns>
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

    /// <summary>
    /// Writes the wrapped raw JSON content verbatim, or a JSON null when the content is null or blank.
    /// </summary>
    /// <param name="writer">The writer to emit JSON to.</param>
    /// <param name="value">The <see cref="RawJson"/> whose content is written as-is.</param>
    /// <param name="options">The serializer options in effect.</param>
    public override void Write(Utf8JsonWriter writer, RawJson value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value?.Content))
            writer.WriteNullValue();
        else
            writer.WriteRawValue(value.Content);
    }
}
