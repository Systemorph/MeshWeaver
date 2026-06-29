using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// Write-only JSON converter for <see cref="JsonNode"/> values, emitting the node's
/// existing JSON tree verbatim. Reading is not supported.
/// </summary>
public class JsonNodeConverter : JsonConverter<JsonNode>
{
    /// <summary>
    /// Indicates that this converter handles <see cref="JsonNode"/> and any of its subtypes.
    /// </summary>
    /// <param name="typeToConvert">The candidate type.</param>
    /// <returns><c>true</c> when <paramref name="typeToConvert"/> is assignable to <see cref="JsonNode"/>.</returns>
    public override bool CanConvert(Type typeToConvert)
        => typeof(JsonNode).IsAssignableFrom(typeToConvert);

    /// <summary>
    /// Not supported — this converter does not deserialize JSON into a <see cref="JsonNode"/>.
    /// </summary>
    /// <param name="reader">The reader (unused).</param>
    /// <param name="typeToConvert">The target type (unused).</param>
    /// <param name="options">The active serializer options (unused).</param>
    /// <returns>Never returns; always throws.</returns>
    /// <exception cref="NotImplementedException">Always thrown.</exception>
    public override JsonNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Writes the supplied <see cref="JsonNode"/> directly to the output, or a JSON null
    /// when the value is null.
    /// </summary>
    /// <param name="writer">The writer to emit the node to.</param>
    /// <param name="value">The node to serialize; may be null.</param>
    /// <param name="options">The active serializer options.</param>
    public override void Write(Utf8JsonWriter writer, JsonNode value, JsonSerializerOptions options)
    {
        if(value is not null)
            value.WriteTo(writer);
        else
            writer.WriteNullValue();
    }
}
