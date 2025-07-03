﻿using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.More;
using MeshWeaver.Domain;
using MeshWeaver.Messaging.Serialization;

namespace MeshWeaver.Data.Serialization;

public class InstanceCollectionConverter(ITypeRegistry typeRegistry)
    : JsonConverter<InstanceCollection>
{
    public const string CollectionProperty = "$type";

    private JsonNode Serialize(InstanceCollection instances, JsonSerializerOptions options)
    {
        return new JsonObject(
            instances.Instances.Select(x => new KeyValuePair<string, JsonNode>(
                JsonSerializer.Serialize(x.Key, options),
                JsonSerializer.SerializeToNode(x.Value, options)
            ))
        );
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
        var type = collection == null 
            ? typeof(object)
            : typeRegistry.GetKeyFunction(collection)?.KeyType 
              ?? typeof(object);
        return new InstanceCollection
        {
            Instances = obj.Where(i => i.Key != CollectionProperty )
                .Select(i => new KeyValuePair<object, object>(
                    JsonSerializer.Deserialize(i.Key, type, options) ?? i.Key,
                    i.Value.Deserialize<object>(options) ?? new object()
                ))
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
