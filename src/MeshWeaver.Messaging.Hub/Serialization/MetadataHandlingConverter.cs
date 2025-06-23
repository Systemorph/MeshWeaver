using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// A converter that strips out metadata properties like $id, $ref during deserialization
/// to prevent issues with JSON that has these properties in unexpected positions.
/// </summary>
public class MetadataStrippingConverter<T> : JsonConverter<T>
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeof(T).IsAssignableFrom(typeToConvert);
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Parse the JSON into a JsonNode first
        using var doc = JsonDocument.ParseValue(ref reader);
        var element = doc.RootElement;

        if (element.ValueKind == JsonValueKind.Object)
        {
            // Create a new JsonObject without metadata properties
            var cleanedJson = StripMetadataProperties(element);

            // Create new options without this converter to avoid infinite recursion
            var newOptions = new JsonSerializerOptions(options);
            for (int i = newOptions.Converters.Count - 1; i >= 0; i--)
            {
                if (newOptions.Converters[i] is MetadataStrippingConverter<T>)
                {
                    newOptions.Converters.RemoveAt(i);
                }
            }

            // Deserialize the cleaned JSON
            return cleanedJson.Deserialize<T>(newOptions);
        }

        // For non-object types, deserialize normally
        var newOptionsSimple = new JsonSerializerOptions(options);
        for (int i = newOptionsSimple.Converters.Count - 1; i >= 0; i--)
        {
            if (newOptionsSimple.Converters[i] is MetadataStrippingConverter<T>)
            {
                newOptionsSimple.Converters.RemoveAt(i);
            }
        }

        var json = element.GetRawText();
        return JsonSerializer.Deserialize<T>(json, newOptionsSimple);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        // Create new options without this converter to avoid infinite recursion
        var newOptions = new JsonSerializerOptions(options);
        for (int i = newOptions.Converters.Count - 1; i >= 0; i--)
        {
            if (newOptions.Converters[i] is MetadataStrippingConverter<T>)
            {
                newOptions.Converters.RemoveAt(i);
            }
        }

        JsonSerializer.Serialize(writer, value, newOptions);
    }

    private static JsonObject StripMetadataProperties(JsonElement element)
    {
        var result = new JsonObject();

        // Add only non-metadata properties
        var metadataProps = new[] { "$id", "$ref", "$values", "$defs" };

        foreach (var property in element.EnumerateObject())
        {
            if (!metadataProps.Contains(property.Name))
            {
                result[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        return result;
    }
}
