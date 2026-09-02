using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Domain;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// Custom converter for IMessageDelivery interface that handles polymorphic deserialization
/// by looking for the concrete MessageDelivery type information.
/// </summary>
public class MessageDeliveryConverter(ITypeRegistry typeRegistry) : JsonConverter<IMessageDelivery>
{
    /// <summary>
    /// Indicates that this converter handles only the <see cref="IMessageDelivery"/> interface itself.
    /// </summary>
    /// <param name="typeToConvert">The candidate type.</param>
    /// <returns><c>true</c> when <paramref name="typeToConvert"/> is exactly <see cref="IMessageDelivery"/>.</returns>
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert == typeof(IMessageDelivery);
    }

    /// <summary>
    /// Reads an <see cref="IMessageDelivery"/>, using the "$type" discriminator and the
    /// type registry to deserialize into the concrete delivery type; when the type is
    /// missing or unknown, falls back to a MessageDelivery carrying the raw JSON message.
    /// </summary>
    /// <param name="reader">The reader positioned at the delivery object.</param>
    /// <param name="typeToConvert">The target type (the <see cref="IMessageDelivery"/> interface).</param>
    /// <param name="options">The active serializer options.</param>
    /// <returns>The deserialized message delivery.</returns>
    public override IMessageDelivery Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // 🚨 NEVER `root.GetRawText()` HERE — issues #3046 / #3047.
        //
        // Both branches used to hand `JsonSerializer.Deserialize` a STRING obtained from
        // GetRawText(). That is not a cheap accessor: it transcodes the whole envelope — payload
        // included — from the document's pooled UTF-8 buffer into a fresh UTF-16 string
        // (JsonReaderHelper.TranscodeHelper, 2× the bytes, on the large-object heap), and then
        // Deserialize(string) transcodes it straight BACK to UTF-8 into a rented buffer (up to 3×)
        // to parse it a second time. Every Orleans deep copy of an IMessageDelivery goes through
        // here, so a single mesh hop was allocating roughly six times the payload to copy it once.
        // On 2026-09-02 that is the arithmetic behind a delivery well under MaxMessageBodySize
        // exhausting a portal pod: the guard measured the message, but the copy allocated a
        // multiple of it. `JsonElement.Deserialize` reaches the same object from the SAME pooled
        // UTF-8 bytes, with no transcode in either direction.
        //
        // This does not make the router allocation-safe at any size — see
        // Doc/Architecture/OversizedDeliveryRefusal, "Where the bound still cannot reach". It
        // removes the self-inflicted multiple; the producer must still not build the payload whole.

        // Check if this object has a type discriminator
        if (root.TryGetProperty(EntitySerializationExtensions.TypeProperty, out var typeElement))
        {
            var typeName = typeElement.GetString();
            if (!string.IsNullOrEmpty(typeName) && typeRegistry.TryGetType(typeName, out var typeInfo))
            {
                // Deserialize to the specific type
                return (IMessageDelivery)root.Deserialize(typeInfo!.Type, options)!;
            }
        }

        // If no type discriminator, try to deserialize as a generic MessageDelivery with RawJson message
        // This is a fallback for cases where the specific type isn't available
        return root.Deserialize<MessageDelivery<RawJson>>(options)!;
    }

    /// <summary>
    /// Writes the delivery using its concrete runtime type so the polymorphic "$type"
    /// discriminator is emitted for round-tripping on read.
    /// </summary>
    /// <param name="writer">The writer to emit the delivery to.</param>
    /// <param name="value">The message delivery to serialize.</param>
    /// <param name="options">The active serializer options.</param>
    public override void Write(Utf8JsonWriter writer, IMessageDelivery value, JsonSerializerOptions options)
    {
        // Serialize using the actual type of the value
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
