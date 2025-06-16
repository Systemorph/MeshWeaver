using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Messaging.Serialization;

namespace MeshWeaver.Layout.Serialization;

/// <summary>
/// Custom JSON converter for Option types that ensures $type discriminators are always included.
/// This converter addresses the polymorphic serialization issues with Option/Option&lt;T&gt; inheritance.
/// </summary>
public class OptionConverter : JsonConverter<Option>
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeof(Option).IsAssignableFrom(typeToConvert);
    }

    public override Option Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected StartObject token");
        }

        using var jsonDocument = JsonDocument.ParseValue(ref reader);
        var jsonObject = jsonDocument.RootElement;

        // Check for $type discriminator
        if (!jsonObject.TryGetProperty(EntitySerializationExtensions.TypeProperty, out var typeProperty))
        {
            throw new JsonException($"Missing required '{EntitySerializationExtensions.TypeProperty}' property for Option deserialization");
        }

        var typeName = typeProperty.GetString();
        if (string.IsNullOrEmpty(typeName))
        {
            throw new JsonException($"Invalid '{EntitySerializationExtensions.TypeProperty}' property value");
        }

        // Parse the type name to determine the concrete Option<T> type
        Type concreteType = null;
        
        // Handle both simple type names and full type names
        if (typeName.StartsWith("MeshWeaver.Layout.Option`1[") && typeName.EndsWith("]"))
        {
            // Extract the generic type argument from the type name
            var genericArg = typeName.Substring("MeshWeaver.Layout.Option`1[".Length);
            genericArg = genericArg.Substring(0, genericArg.Length - 1); // Remove the closing ]
            
            // Map common type names
            var itemType = genericArg switch
            {
                "System.String" or "String" => typeof(string),
                "System.Int32" or "Int32" => typeof(int),
                "System.Boolean" or "Boolean" => typeof(bool),
                "System.Double" or "Double" => typeof(double),
                "System.Decimal" or "Decimal" => typeof(decimal),
                _ => Type.GetType(genericArg) ?? typeof(object)
            };
            
            concreteType = typeof(Option<>).MakeGenericType(itemType);
        }
        else if (typeName == "MeshWeaver.Layout.Option" || typeName == "Option")
        {
            // For the abstract Option type, we need to infer the type from the content
            // This is a fallback case - ideally the $type should specify the concrete type
            throw new JsonException("Cannot deserialize abstract Option type without concrete type information");
        }
        else
        {
            // Try to resolve the type directly
            concreteType = Type.GetType(typeName);
        }

        if (concreteType == null || !typeof(Option).IsAssignableFrom(concreteType))
        {
            throw new JsonException($"Unable to resolve Option type from '{typeName}'");
        }        // Use the default serializer to deserialize as the concrete type
        var converterOptions = new JsonSerializerOptions(options);
        // Remove this converter to prevent infinite recursion
        for (int i = converterOptions.Converters.Count - 1; i >= 0; i--)
        {
            if (converterOptions.Converters[i] is OptionConverter)
            {
                converterOptions.Converters.RemoveAt(i);
            }
        }
        
        return (Option)JsonSerializer.Deserialize(jsonObject.GetRawText(), concreteType, converterOptions);
    }

    public override void Write(Utf8JsonWriter writer, Option value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        // Always serialize with the concrete runtime type to ensure $type is included
        var concreteType = value.GetType();
          var converterOptions = new JsonSerializerOptions(options);
        // Remove this converter to prevent infinite recursion
        for (int i = converterOptions.Converters.Count - 1; i >= 0; i--)
        {
            if (converterOptions.Converters[i] is OptionConverter)
            {
                converterOptions.Converters.RemoveAt(i);
            }
        }
        
        // Serialize as the concrete type, which will include the $type discriminator
        JsonSerializer.Serialize(writer, value, concreteType, converterOptions);
    }
}
