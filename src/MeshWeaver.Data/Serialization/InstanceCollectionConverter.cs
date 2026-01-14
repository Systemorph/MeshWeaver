using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.More;
using MeshWeaver.Domain;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data.Serialization;

public class InstanceCollectionConverter(ITypeRegistry typeRegistry, ILogger<InstanceCollectionConverter>? logger = null)
    : JsonConverter<InstanceCollection>
{
    public const string CollectionProperty = "$type";

    private JsonNode Serialize(InstanceCollection instances, JsonSerializerOptions options)
    {
        return new JsonObject(
            instances.Instances.Select(x => new KeyValuePair<string, JsonNode?>(
                ConvertKeyToString(x.Key, options),
                JsonSerializer.SerializeToNode(x.Value, options)
            ))
        );
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

        logger?.LogDebug("InstanceCollectionConverter.Read: collection={Collection}, keyFunction={KeyFunction}, keyType={KeyType}",
            collection, keyFunction != null ? "found" : "null", type.Name);

        return new InstanceCollection
        {
            Instances = obj.Where(i => i.Key != CollectionProperty)
                .Select(i =>
                {
                    try
                    {
                        var deserializedKey = ConvertStringToKey(i.Key, type, options);
                        var deserializedValue = i.Value.Deserialize<object>(options) ?? new object();
                        return new KeyValuePair<object, object>(deserializedKey, deserializedValue);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex, "InstanceCollectionConverter.Read: Failed to deserialize key={Key} as type={Type}. Raw key: '{RawKey}'",
                            i.Key, type.Name, i.Key);
                        throw;
                    }
                })
                .ToImmutableDictionary()
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        InstanceCollection value,
        JsonSerializerOptions options
    )
    {
        Serialize(value, options).WriteTo(writer);
    }
}
