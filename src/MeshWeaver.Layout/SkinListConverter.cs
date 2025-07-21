using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Domain;

namespace MeshWeaver.Layout;

/// <summary>
/// Custom converter for ImmutableList of Skin objects that handles empty objects gracefully.
/// </summary>
public class SkinListConverter : JsonConverter<ImmutableList<Skin>>
{
    private readonly ITypeRegistry? _typeRegistry;

    public SkinListConverter(ITypeRegistry typeRegistry)
    {
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
    }

    // Parameterless constructor for use with JsonConverterAttribute
    public SkinListConverter()
    {
        // Type registry will be null - we'll need to handle this case
        _typeRegistry = null!;
    }

    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert == typeof(ImmutableList<Skin>);
    }

    public override ImmutableList<Skin> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return ImmutableList<Skin>.Empty;

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected StartArray token, got {reader.TokenType}");

        var result = ImmutableList<Skin>.Empty.ToBuilder();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                break;

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                var root = doc.RootElement;

                // Check if this is an empty object
                if (root.ValueKind == JsonValueKind.Object && !root.EnumerateObject().Any())
                {
                    // Skip empty objects instead of adding null
                    continue;
                }

                // Check if this object has a type discriminator
                if (root.TryGetProperty("$type", out var typeElement))
                {
                    var typeName = typeElement.GetString();
                    if (!string.IsNullOrEmpty(typeName))
                    {
                        Type? skinType = null;

                        // Try to get type from registry first, then fall back to Type.GetType
                        if (_typeRegistry?.TryGetType(typeName, out var typeInfo) == true)
                        {
                            skinType = typeInfo!.Type;
                        }
                        else
                        {
                            skinType = Type.GetType(typeName!);
                        }

                        if (skinType != null && typeof(Skin).IsAssignableFrom(skinType))
                        {
                            // Deserialize to the specific skin type
                            var json = root.GetRawText();
                            var skin = (Skin?)JsonSerializer.Deserialize(json, skinType, options);
                            if (skin != null)
                                result.Add(skin);
                        }
                    }
                }
                // If no type discriminator or unknown type, skip the item
            }
            else if (reader.TokenType == JsonTokenType.Null)
            {
                // Skip null values
                continue;
            }
        }

        return result.ToImmutable();
    }

    public override void Write(Utf8JsonWriter writer, ImmutableList<Skin> value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var skin in value.Where(s => s != null)) // Filter out null values during serialization
        {
            JsonSerializer.Serialize(writer, skin, skin.GetType(), options);
        }
        writer.WriteEndArray();
    }
}
