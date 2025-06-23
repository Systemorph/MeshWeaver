using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// Generic converter factory for read-only collection interfaces that handles polymorphic deserialization
/// by deserializing to concrete implementations using arrays.
/// Supports IReadOnlyCollection&lt;T&gt;, IReadOnlyList&lt;T&gt;, IEnumerable&lt;T&gt;, etc.
/// </summary>
public class ReadOnlyCollectionConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        if (!typeToConvert.IsInterface || !typeToConvert.IsGenericType)
            return false;

        var genericTypeDefinition = typeToConvert.GetGenericTypeDefinition();

        return genericTypeDefinition == typeof(IReadOnlyCollection<>) ||
               genericTypeDefinition == typeof(IReadOnlyList<>) ||
               genericTypeDefinition == typeof(IEnumerable<>);
    }

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
                    // The polymorphic resolver should handle the $type discriminators
                    var item = JsonSerializer.Deserialize<T>(element.GetRawText(), options);
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
    public override IReadOnlyList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
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
                var item = JsonSerializer.Deserialize<T>(element.GetRawText(), options);
                if (item != null)
                    list.Add(item);
            }
            return new ReadOnlyCollection<T>(list);
        }
        else
        {
            var array = JsonSerializer.Deserialize<T[]>(ref reader, options);
            return array == null ? new ReadOnlyCollection<T>(Array.Empty<T>()) : new ReadOnlyCollection<T>(array);
        }
    }
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
            var item = JsonSerializer.Deserialize<T>(element.GetRawText(), options);
            if (item != null)
                list.Add(item);
        }
        return list.ToArray();
    }
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
