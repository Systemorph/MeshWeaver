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
    /// Skin types that are not resolvable via <see cref="Type.GetType(string)"/> will be skipped.
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
                        // Resolve the concrete skin type from its $type discriminator. The discriminator now
                        // defaults to the SHORT type name (TypeRegistry.FormatType, fb2ee677d), which is
                        // resolvable ONLY through the type registry — so a skin's type MUST be registered on
                        // BOTH the sending and receiving hub. Resolution order:
                        //   1. this converter's registry (short names, and legacy full names via the registry
                        //      alias) — used by the options-registered instance (LayoutExtensions.AddLayoutTypes);
                        //   2. Type.GetType — the [JsonConverter]-attribute instance has no registry, but a legacy
                        //      namespace-qualified FULL name still resolves by reflection;
                        //   3. STJ's registry-backed polymorphism on the base Skin — the attribute path's
                        //      short-name case: the hub's PolymorphicTypeInfoResolver keys Skin's derived types
                        //      by the same short names, so resolution still flows through the registry.
                        var json = root.GetRawText();
                        Type? skinType =
                            _typeRegistry?.TryGetType(typeName, out var typeInfo) == true
                                ? typeInfo!.Type
                                : Type.GetType(typeName!);

                        Skin? skin = null;
                        if (skinType != null && typeof(Skin).IsAssignableFrom(skinType))
                        {
                            skin = (Skin?)JsonSerializer.Deserialize(json, skinType, options);
                        }
                        else
                        {
                            // No registry on this instance and not a full name → resolve the SHORT name through
                            // the hub's polymorphism resolver. An unregistered skin stays unresolved and is
                            // skipped (the same graceful degradation as a missing $type — the serialize side
                            // already warns loudly on unregistered types via WarnUnregisteredSerialization).
                            try
                            {
                                skin = (Skin?)JsonSerializer.Deserialize(json, typeof(Skin), options);
                            }
                            catch (NotSupportedException)
                            {
                                // Abstract Skin + unrecognized discriminator (unregistered on this hub) → skip.
                            }
                        }

                        if (skin != null)
                            result.Add(skin);
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
