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

        // Check if this object has a type discriminator
        if (root.TryGetProperty(EntitySerializationExtensions.TypeProperty, out var typeElement))
        {
            var typeName = typeElement.GetString();
            if (!string.IsNullOrEmpty(typeName) && typeRegistry.TryGetType(typeName, out var typeInfo))
            {
                // Deserialize to the specific type
                var json = root.GetRawText();
                return (IMessageDelivery)JsonSerializer.Deserialize(json, typeInfo!.Type, options)!;
            }
        }

        // If no type discriminator, try to deserialize as a generic MessageDelivery with RawJson message
        // This is a fallback for cases where the specific type isn't available
        var json2 = root.GetRawText();
        return JsonSerializer.Deserialize<MessageDelivery<RawJson>>(json2, options)!;
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
