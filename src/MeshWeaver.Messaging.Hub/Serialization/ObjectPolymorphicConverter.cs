using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Domain;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// Custom converter for object types that handles polymorphic serialization/deserialization
/// using the type registry and $type discriminator.
/// </summary>
public class ObjectPolymorphicConverter(ITypeRegistry typeRegistry) : JsonConverter<object>
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert == typeof(object);
    }

    public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Handle different JSON token types
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                // Try to determine if it's an integer or decimal
                if (reader.TryGetInt32(out var intValue))
                    return intValue;
                if (reader.TryGetInt64(out var longValue))
                    return longValue;
                return reader.GetDouble();
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.StartObject:
                return ReadObject(ref reader, options);
            case JsonTokenType.StartArray:
                // For arrays, deserialize as JsonElement and let the application handle it
                using (var doc = JsonDocument.ParseValue(ref reader))
                {
                    return doc.RootElement.Clone();
                }
            default:
                throw new JsonException($"Unexpected token type: {reader.TokenType}");
        }
    }
    private object ReadObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Check if this object has a type discriminator
        if (root.TryGetProperty(EntitySerializationExtensions.TypeProperty, out var typeElement))
        {
            var typeName = typeElement.GetString();
            if (!string.IsNullOrEmpty(typeName) && typeRegistry.TryGetType(typeName, out var typeInfo))
            {
                try
                {
                    // Deserialize to the specific type
                    var json = root.GetRawText();
                    return JsonSerializer.Deserialize(json, typeInfo.Type, options);
                }
                catch (NotSupportedException ex) when (ex.Message.Contains("polymorphic interface or abstract type"))
                {
                    // If the target type is abstract/interface and requires polymorphic deserialization,
                    // but the JSON is missing proper discriminator, return as JsonElement
                    return root.Clone();
                }
            }
        }

        // If no type discriminator or unknown type, return as JsonElement
        return root.Clone();
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        var valueType = value.GetType();

        // For primitive types, write directly
        if (valueType == typeof(string))
        {
            writer.WriteStringValue((string)value);
        }
        else if (valueType == typeof(int))
        {
            writer.WriteNumberValue((int)value);
        }
        else if (valueType == typeof(long))
        {
            writer.WriteNumberValue((long)value);
        }
        else if (valueType == typeof(double))
        {
            writer.WriteNumberValue((double)value);
        }
        else if (valueType == typeof(float))
        {
            writer.WriteNumberValue((float)value);
        }
        else if (valueType == typeof(decimal))
        {
            writer.WriteNumberValue((decimal)value);
        }
        else if (valueType == typeof(bool))
        {
            writer.WriteBooleanValue((bool)value);
        }
        else
        {
            // Check if this type has a specific converter that should handle it
            if (HasSpecificConverter(valueType, options))
            {
                // Let the specific converter handle it without adding type information
                JsonSerializer.Serialize(writer, value, valueType, options);
            }
            // For complex types, serialize with type information if registered
            else if (typeRegistry.TryGetCollectionName(valueType, out var typeName))
            {
                // Create a wrapper object with type discriminator
                var json = JsonSerializer.Serialize(value, valueType, options);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    writer.WriteStartObject();
                    writer.WriteString(EntitySerializationExtensions.TypeProperty, typeName);

                    // Copy all properties from the original object
                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        if (property.Name != EntitySerializationExtensions.TypeProperty)
                        {
                            property.WriteTo(writer);
                        }
                    }

                    writer.WriteEndObject();
                }
                else
                {
                    // For non-object types, just serialize normally
                    JsonSerializer.Serialize(writer, value, valueType, options);
                }
            }
            else
            {
                // Type not registered, serialize without type information
                JsonSerializer.Serialize(writer, value, valueType, options);
            }
        }
    }

    private static bool HasSpecificConverter(Type valueType, JsonSerializerOptions options)
    {
        // Check if there's a specific converter for this type
        foreach (var converter in options.Converters)
        {
            if (converter.CanConvert(valueType) && converter.GetType() != typeof(ObjectPolymorphicConverter))
            {
                return true;
            }
        }
        return false;
    }
}
