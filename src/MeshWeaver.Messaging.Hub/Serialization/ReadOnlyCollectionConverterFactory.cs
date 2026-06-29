using System.Buffers;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// Reorders JSON object properties so that $type appears first, which is required
/// by System.Text.Json for polymorphic types with parameterized constructors.
/// Returns the original raw text if no reordering is needed.
/// </summary>
internal static class JsonElementNormalizer
{
    private const string TypeDiscriminator = "$type";

    public static string GetNormalizedRawText(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return element.GetRawText();

        if (!element.TryGetProperty(TypeDiscriminator, out _))
            return element.GetRawText();

        // Check if $type is already the first property
        using var enumerator = element.EnumerateObject();
        if (!enumerator.MoveNext())
            return element.GetRawText();

        if (enumerator.Current.Name == TypeDiscriminator)
            return element.GetRawText(); // Already first, no work needed

        // Reorder: write $type first, then all other properties
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(TypeDiscriminator);
            element.GetProperty(TypeDiscriminator).WriteTo(writer);
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name == TypeDiscriminator)
                    continue;
                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>
/// Generic converter factory for read-only collection interfaces that handles polymorphic deserialization
/// by deserializing to concrete implementations using arrays.
/// Supports IReadOnlyCollection&lt;T&gt;, IReadOnlyList&lt;T&gt;, IEnumerable&lt;T&gt;, etc.
/// </summary>
public class ReadOnlyCollectionConverterFactory : JsonConverterFactory
{
    /// <summary>
    /// Determines whether the requested type is a supported generic read-only collection interface
    /// (<see cref="IReadOnlyCollection{T}"/>, <see cref="IReadOnlyList{T}"/>, or <see cref="IEnumerable{T}"/>).
    /// </summary>
    /// <param name="typeToConvert">The type being considered for conversion.</param>
    /// <returns><c>true</c> if a converter can be produced for the type; otherwise <c>false</c>.</returns>
    public override bool CanConvert(Type typeToConvert)
    {
        if (!typeToConvert.IsInterface || !typeToConvert.IsGenericType)
            return false;

        var genericTypeDefinition = typeToConvert.GetGenericTypeDefinition();

        return genericTypeDefinition == typeof(IReadOnlyCollection<>) ||
               genericTypeDefinition == typeof(IReadOnlyList<>) ||
               genericTypeDefinition == typeof(IEnumerable<>);
    }

    /// <summary>
    /// Creates the concrete converter matching the requested collection interface, constructed over
    /// the interface's element type.
    /// </summary>
    /// <param name="typeToConvert">The collection interface type to create a converter for.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>A converter for the requested collection interface.</returns>
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var elementType = typeToConvert.GetGenericArguments()[0];
        var genericTypeDefinition = typeToConvert.GetGenericTypeDefinition();

        Type converterType;
        if (genericTypeDefinition == typeof(IReadOnlyCollection<>))
        {
            converterType = typeof(ReadOnlyCollectionConverter<>).MakeGenericType(elementType);
        }
        else if (genericTypeDefinition == typeof(IReadOnlyList<>))
        {
            converterType = typeof(ReadOnlyListConverter<>).MakeGenericType(elementType);
        }
        else if (genericTypeDefinition == typeof(IEnumerable<>))
        {
            converterType = typeof(EnumerableConverter<>).MakeGenericType(elementType);
        }
        else
        {
            throw new NotSupportedException($"Type {typeToConvert} is not supported by ReadOnlyCollectionConverterFactory");
        }

        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

/// <summary>
/// Converter for IReadOnlyCollection&lt;T&gt;
/// </summary>
/// <typeparam name="T">The element type of the collection</typeparam>
public class ReadOnlyCollectionConverter<T> : JsonConverter<IReadOnlyCollection<T>>
{
    /// <summary>
    /// Reads a JSON array into a read-only collection. For abstract/interface/polymorphic element types
    /// each element is deserialized individually (normalizing $type to the first position) so concrete
    /// types resolve correctly; otherwise the array is deserialized in one pass.
    /// </summary>
    /// <param name="reader">The reader positioned at the JSON array.</param>
    /// <param name="typeToConvert">The target collection type.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>A read-only collection of <typeparamref name="T"/>; empty when the JSON is not an array.</returns>
    public override IReadOnlyCollection<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // For abstract types, interfaces, or types with polymorphic attributes, we need to deserialize each element individually
        // using the polymorphic resolver to handle the actual concrete types
        if (typeof(T).IsAbstract || typeof(T).IsInterface || HasPolymorphicAttributes(typeof(T)))
        {
            using var jsonDoc = JsonDocument.ParseValue(ref reader);
            if (jsonDoc.RootElement.ValueKind != JsonValueKind.Array)
                return new ReadOnlyCollection<T>(Array.Empty<T>());

            var list = new List<T>();
            foreach (var element in jsonDoc.RootElement.EnumerateArray())
            {
                try
                {
                    // Deserialize each element using the proper JsonSerializerOptions
                    // Normalize to ensure $type is first (required for parameterized constructor types)
                    var item = JsonSerializer.Deserialize<T>(JsonElementNormalizer.GetNormalizedRawText(element), options);
                    if (item != null)
                        list.Add(item);
                }
                catch (NotSupportedException ex) when (ex.Message.Contains("Deserialization of interface or abstract types"))
                {
                    // This indicates missing $type discriminator - the serialization side needs to be fixed
                    throw new InvalidOperationException(
                        $"Failed to deserialize {typeof(T).Name} from JSON. " +
                        "This likely indicates that the serialization did not include the required $type discriminator for polymorphic types. " +
                        $"JSON element: {element.GetRawText()}", ex);
                }
            }
            return new ReadOnlyCollection<T>(list);
        }
        else
        {
            var array = JsonSerializer.Deserialize<T[]>(ref reader, options);
            return array == null ? new ReadOnlyCollection<T>(Array.Empty<T>()) : new ReadOnlyCollection<T>(array);
        }
    }
    /// <summary>
    /// Writes a read-only collection as a JSON array. For abstract/interface/polymorphic element types
    /// each element is serialized by its runtime type so the polymorphic converter can attach $type;
    /// otherwise the collection is written as a single array.
    /// </summary>
    /// <param name="writer">The writer to emit JSON to.</param>
    /// <param name="value">The collection to serialize; <c>null</c> is written as a JSON null.</param>
    /// <param name="options">The serializer options in effect.</param>
    public override void Write(Utf8JsonWriter writer, IReadOnlyCollection<T> value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        // For abstract types, interfaces, or types with polymorphic attributes, serialize each element individually
        // so the polymorphic converter can add $type information
        if (typeof(T).IsAbstract || typeof(T).IsInterface || HasPolymorphicAttributes(typeof(T)))
        {
            writer.WriteStartArray();
            foreach (var item in value)
            {
                // Use the actual type of the item instead of T to ensure polymorphic serialization
                JsonSerializer.Serialize(writer, item, item?.GetType() ?? typeof(T), options);
            }
            writer.WriteEndArray();
        }
        else
        {
            JsonSerializer.Serialize(writer, value.ToArray(), typeof(T[]), options);
        }
    }

    private static bool HasPolymorphicAttributes(Type type)
    {
        return type.GetCustomAttributes(typeof(JsonPolymorphicAttribute), inherit: true).Any();
    }
}

/// <summary>
/// Converter for IReadOnlyList&lt;T&gt;
/// </summary>
/// <typeparam name="T">The element type of the list</typeparam>
public class ReadOnlyListConverter<T> : JsonConverter<IReadOnlyList<T>>
{
    /// <summary>
    /// Reads a JSON array into a read-only list, deserializing each element individually via a
    /// <see cref="JsonDocument"/> and normalizing $type to the first position so legacy data with the
    /// discriminator in a non-leading position still deserializes.
    /// </summary>
    /// <param name="reader">The reader positioned at the JSON array.</param>
    /// <param name="typeToConvert">The target list type.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>A read-only list of <typeparamref name="T"/>; empty when the JSON is not an array.</returns>
    public override IReadOnlyList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Always parse each element individually via JsonDocument to handle
        // $type metadata in any position (old data in PostgreSQL may have
        // $type not as first property, which breaks Deserialize<T[]>).
        using var jsonDoc = JsonDocument.ParseValue(ref reader);
        if (jsonDoc.RootElement.ValueKind != JsonValueKind.Array)
            return new ReadOnlyCollection<T>(Array.Empty<T>());

        var list = new List<T>();
        foreach (var element in jsonDoc.RootElement.EnumerateArray())
        {
            var item = JsonSerializer.Deserialize<T>(JsonElementNormalizer.GetNormalizedRawText(element), options);
            if (item != null)
                list.Add(item);
        }
        return new ReadOnlyCollection<T>(list);
    }
    /// <summary>
    /// Writes a read-only list as a JSON array. For abstract/interface/polymorphic element types each
    /// element is serialized by its runtime type so the polymorphic converter can attach $type;
    /// otherwise the list is written as a single array.
    /// </summary>
    /// <param name="writer">The writer to emit JSON to.</param>
    /// <param name="value">The list to serialize; <c>null</c> is written as a JSON null.</param>
    /// <param name="options">The serializer options in effect.</param>
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<T> value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        // For abstract types, interfaces, or types with polymorphic attributes, serialize each element individually
        // so the polymorphic converter can add $type information
        if (typeof(T).IsAbstract || typeof(T).IsInterface || HasPolymorphicAttributes(typeof(T)))
        {
            writer.WriteStartArray();
            foreach (var item in value)
            {
                // Use the actual type of the item instead of T to ensure polymorphic serialization
                JsonSerializer.Serialize(writer, item, item?.GetType() ?? typeof(T), options);
            }
            writer.WriteEndArray();
        }
        else
        {
            JsonSerializer.Serialize(writer, value.ToArray(), typeof(T[]), options);
        }
    }

    private static bool HasPolymorphicAttributes(Type type)
    {
        return type.GetCustomAttributes(typeof(JsonPolymorphicAttribute), inherit: true).Any();
    }
}

/// <summary>
/// Converter for IEnumerable&lt;T&gt;
/// </summary>
/// <typeparam name="T">The element type of the enumerable</typeparam>
public class EnumerableConverter<T> : JsonConverter<IEnumerable<T>>
{
    /// <summary>
    /// Reads a JSON array into an enumerable (materialized as an array), deserializing each element
    /// individually via a <see cref="JsonDocument"/> and normalizing $type to the first position so
    /// concrete polymorphic types resolve correctly.
    /// </summary>
    /// <param name="reader">The reader positioned at the JSON array.</param>
    /// <param name="typeToConvert">The target enumerable type.</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>An enumerable of <typeparamref name="T"/>; empty when the JSON is not an array.</returns>
    public override IEnumerable<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // For abstract types, interfaces, or types with polymorphic attributes, we need to deserialize each element individually
        // using the polymorphic resolver to handle the actual concrete types
        using var jsonDoc = JsonDocument.ParseValue(ref reader);
        if (jsonDoc.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<T>();
        foreach (var element in jsonDoc.RootElement.EnumerateArray())
        {
            var item = JsonSerializer.Deserialize<T>(JsonElementNormalizer.GetNormalizedRawText(element), options);
            if (item != null)
                list.Add(item);
        }
        return list.ToArray();
    }
    /// <summary>
    /// Writes an enumerable as a JSON array, serializing each element by its runtime type so the
    /// polymorphic converter can attach $type where needed.
    /// </summary>
    /// <param name="writer">The writer to emit JSON to.</param>
    /// <param name="value">The enumerable to serialize; <c>null</c> is written as a JSON null.</param>
    /// <param name="options">The serializer options in effect.</param>
    public override void Write(Utf8JsonWriter writer, IEnumerable<T> value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value)
        {
            // Use the actual type of the item instead of T to ensure polymorphic serialization
            JsonSerializer.Serialize(writer, item, item?.GetType() ?? typeof(T), options);
        }
        writer.WriteEndArray();
    }

}
