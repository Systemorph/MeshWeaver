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
    /// <summary>
    /// Determines whether this converter can handle the requested type, i.e. whether
    /// <typeparamref name="T"/> is assignable from <paramref name="typeToConvert"/>.
    /// </summary>
    /// <param name="typeToConvert">The type being considered for conversion.</param>
    /// <returns><c>true</c> if the type can be converted by this converter; otherwise <c>false</c>.</returns>
    public override bool CanConvert(Type typeToConvert)
    {
        return typeof(T).IsAssignableFrom(typeToConvert);
    }

    /// <summary>
    /// Reads JSON into a <typeparamref name="T"/>, first stripping reference-handling
    /// metadata properties ($id, $ref, $values, $defs) from objects so that JSON carrying
    /// these properties in unexpected positions still deserializes cleanly. Deserialization
    /// runs against a copy of the options with this converter removed to avoid infinite recursion.
    /// </summary>
    /// <param name="reader">The reader positioned at the value to deserialize.</param>
    /// <param name="typeToConvert">The target type to deserialize into.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>The deserialized <typeparamref name="T"/> instance.</returns>
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
            return cleanedJson.Deserialize<T>(newOptions)!;
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
        return JsonSerializer.Deserialize<T>(json, newOptionsSimple)!;
    }

    /// <summary>
    /// Writes <paramref name="value"/> as JSON using a copy of the options with this converter
    /// removed, so the value serializes with the default behaviour and without re-entering this
    /// converter (which would cause infinite recursion).
    /// </summary>
    /// <param name="writer">The writer to emit JSON to.</param>
    /// <param name="value">The value to serialize.</param>
    /// <param name="options">The serializer options in effect.</param>
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
