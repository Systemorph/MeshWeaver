using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.More;
using MeshWeaver.Domain;

namespace MeshWeaver.Data.Serialization;

/// <summary>
/// <see cref="JsonConverter{T}"/> for <see cref="InstanceCollection"/>: serializes each instance as a JSON
/// property keyed by its (JSON-encoded) identity, with a leading <c>$type</c> property naming the collection.
/// Keys are resolved back to their CLR type via the key function registered in <paramref name="typeRegistry"/>.
/// </summary>
/// <param name="typeRegistry">The type registry used to resolve the key type for a collection.</param>
public class InstanceCollectionConverter(ITypeRegistry typeRegistry)
    : JsonConverter<InstanceCollection>
{
    /// <summary>
    /// Name of the JSON property that carries the collection (type) discriminator. Must be written first.
    /// </summary>
    public const string CollectionProperty = "$type";

    private JsonNode Serialize(InstanceCollection instances, JsonSerializerOptions options)
    {
        var jsonObject = new JsonObject();

        // $type MUST be the first property — STJ polymorphic deserializer requires it
        if (instances.CollectionName != null)
        {
            jsonObject[CollectionProperty] = instances.CollectionName;
        }

        foreach (var x in instances.Instances)
        {
            jsonObject[ConvertKeyToString(x.Key, options)] =
                JsonSerializer.SerializeToNode(x.Value, options);
        }

        return jsonObject;
    }

    /// <summary>
    /// Converts a key object to a string for use as a JSON property name.
    /// For simple types (string, numbers), just use ToString().
    /// For complex types (tuples), serialize as JSON.
    /// </summary>
    private static string ConvertKeyToString(object key, JsonSerializerOptions options)
    {
        return JsonSerializer.Serialize(key, options);
    }

    /// <summary>
    /// Converts a JSON property name string back to a key object.
    /// For simple types (string, numbers), parse directly.
    /// For complex types (tuples), deserialize from JSON.
    /// </summary>
    private static object ConvertStringToKey(string keyString, Type keyType, JsonSerializerOptions options)
    {
        return JsonSerializer.Deserialize(keyString, keyType, options) ?? keyString;
    }

    /// <summary>
    /// Reads an <see cref="InstanceCollection"/> from JSON, decoding each property name into a typed key.
    /// </summary>
    /// <param name="reader">The reader positioned at the start of the collection object.</param>
    /// <param name="typeToConvert">The type being converted (always <see cref="InstanceCollection"/>).</param>
    /// <param name="options">The serializer options to use for keys and values.</param>
    /// <returns>The deserialized collection, or null when the JSON is not an object.</returns>
    public override InstanceCollection? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var obj = doc.RootElement.AsNode() as JsonObject;
        if (obj == null)
            return null;
        var collection = obj[CollectionProperty]?.ToString();
        var keyFunction = collection == null ? null : typeRegistry.GetKeyFunction(collection);
        var type = keyFunction?.KeyType ?? typeof(string);  // Default to string, not object

        return new InstanceCollection
        {
            Instances = obj.Where(i => i.Key != CollectionProperty)
                .Select(i =>
                {
                    var deserializedKey = ConvertStringToKey(i.Key, type, options);
                    var deserializedValue = i.Value.Deserialize<object>(options) ?? new object();
                    return new KeyValuePair<object, object>(deserializedKey, deserializedValue);
                })
                .ToImmutableDictionary(),
            CollectionName = collection
        };
    }

    /// <summary>
    /// Writes an <see cref="InstanceCollection"/> to JSON.
    /// </summary>
    /// <param name="writer">The writer to emit the collection to.</param>
    /// <param name="value">The collection to serialize.</param>
    /// <param name="options">The serializer options to use for keys and values.</param>
    public override void Write(
        Utf8JsonWriter writer,
        InstanceCollection value,
        JsonSerializerOptions options
    )
    {
        Serialize(value, options).WriteTo(writer);
    }
}
