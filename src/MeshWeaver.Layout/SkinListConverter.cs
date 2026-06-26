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

    /// <summary>
    /// Initializes the converter with an <see cref="ITypeRegistry"/> used to resolve skin types by name.
    /// </summary>
    /// <param name="typeRegistry">The type registry; must not be null.</param>
    public SkinListConverter(ITypeRegistry typeRegistry)
    {
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
    }

    // Parameterless constructor for use with JsonConverterAttribute
    /// <summary>
    /// Parameterless constructor for use as a <c>[JsonConverter]</c> attribute; type registry will be unavailable.
    /// Skin types that are not resolvable via <see cref="Type.GetType"/> will be skipped.
    /// </summary>
    public SkinListConverter()
    {
        // Type registry will be null - we'll need to handle this case
        _typeRegistry = null!;
    }

    /// <summary>
    /// Returns true when <paramref name="typeToConvert"/> is exactly <see cref="ImmutableList{Skin}"/>.
    /// </summary>
    /// <param name="typeToConvert">The type being inspected by the serializer.</param>
    /// <returns>True if this converter handles the type; otherwise false.</returns>
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert == typeof(ImmutableList<Skin>);
    }

    /// <summary>
    /// Reads a JSON array into an <see cref="ImmutableList{Skin}"/>, using the <c>$type</c> discriminator
    /// on each element to resolve the concrete skin type. Empty objects and null elements are skipped.
    /// </summary>
    /// <param name="reader">The JSON reader positioned at the start of the array.</param>
    /// <param name="typeToConvert">The target collection type (always <see cref="ImmutableList{Skin}"/>).</param>
    /// <param name="options">Serializer options forwarded when deserializing individual skin elements.</param>
    /// <returns>An immutable list of the successfully deserialized skin instances.</returns>
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

    /// <summary>
    /// Writes an <see cref="ImmutableList{Skin}"/> as a JSON array, serializing each element under its
    /// concrete runtime type so that <c>$type</c> discriminators are preserved. Null elements are filtered out.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="value">The skin list to serialize.</param>
    /// <param name="options">Serializer options forwarded when serializing individual skin elements.</param>
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
