using System.Collections.Immutable;
using System.Text.Json;
using Json.More;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public static class WorkspaceJsonSerializer
{
    public static JsonSerializerOptions Options(this ISerializationService serializationService, IReadOnlyDictionary<string, ITypeSource> typeProviders) =>
        new()
        {
            Converters =
            {
                new EntityStoreConverter(typeProviders, serializationService), 
                new InstancesInCollectionConverter(serializationService),
                new SerializationServiceConverter(serializationService)
            }
        };
}

public class SerializationServiceConverter(ISerializationService serializationService) : JsonConverter<object>
{

    public override bool CanConvert(Type typeToConvert)
        => typeToConvert != typeof(EntityStore);

    public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        return serializationService.Deserialize(doc.RootElement.ToString());
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        JsonNode.Parse(serializationService.SerializeToString(value))!.WriteTo(writer);
    }
}

public class EntityStoreConverter(
    IReadOnlyDictionary<string, ITypeSource> typeSourcesByCollection,
    ISerializationService serializationService) : JsonConverter<EntityStore>
{
    public override EntityStore Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        return Deserialize(doc.RootElement.AsNode(), options);
    }

    public override void Write(Utf8JsonWriter writer, EntityStore value, JsonSerializerOptions options)
    {
        Serialize(value, options).WriteTo(writer);
    }

    private JsonNode Serialize(EntityStore store, JsonSerializerOptions options)
    {
        var ret = new JsonObject(
            store.Instances.ToDictionary(
                x => x.Key,
                x => JsonSerializer.SerializeToNode(x.Value, options)
            ));
        return ret;
    }



    public EntityStore Deserialize(JsonNode serializedWorkspace, JsonSerializerOptions options)
    {
        if (serializedWorkspace is not JsonObject obj)
            throw new ArgumentException("Invalid serialized workspace");

        var newStore =
            new EntityStore(obj.Select(kvp => DeserializeCollection(kvp.Key, kvp.Value, options)).ToImmutableDictionary());

        return newStore;
    }

    private KeyValuePair<string, InstanceCollection> DeserializeCollection(string collection, JsonNode node, JsonSerializerOptions options)
    {
        return
            new(
                collection,
                node.Deserialize<InstanceCollection>(options)
            );
    }
}

public class InstancesInCollectionConverter(ISerializationService serializationService) : JsonConverter<InstanceCollection>
{

    private InstanceCollection Deserialize(JsonNode node)
    {
        if (node is not JsonObject obj)
            throw new ArgumentException("Expecting an array");
        return new(obj.Select(jsonNode => DeserializeEntity(jsonNode.Key, jsonNode.Value)).ToImmutableDictionary());
    }

    private KeyValuePair<object, object> DeserializeEntity(string idSerialized, JsonNode jsonNode)
    {
        return new(
            serializationService.Deserialize(idSerialized),
            serializationService.Deserialize(jsonNode.ToJsonString()));
    }

    private JsonNode Serialize(InstanceCollection instances)
    {
        return new JsonObject(instances.Instances.Select(y =>
            new KeyValuePair<string, JsonNode>(serializationService.SerializeToString(y.Key),
                JsonNode.Parse(serializationService.SerializeToString(y.Value))))
        );
    }

    public override InstanceCollection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        return Deserialize(doc.RootElement.AsNode());
    }


    public override void Write(Utf8JsonWriter writer, InstanceCollection value, JsonSerializerOptions options)
    {
        Serialize(value).WriteTo(writer);
    }
}
