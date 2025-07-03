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
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert == typeof(IMessageDelivery);
    }

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

    public override void Write(Utf8JsonWriter writer, IMessageDelivery value, JsonSerializerOptions options)
    {
        // Serialize using the actual type of the value
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
