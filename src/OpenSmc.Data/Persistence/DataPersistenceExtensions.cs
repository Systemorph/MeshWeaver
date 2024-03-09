using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data.Persistence;

public static class DataPersistenceExtensions
{
    public static JsonObject SerializeState(this ISerializationService serializationService, IReadOnlyDictionary<string, InstancesInCollection> data) 
        => new(
            data
                .Select(g => new KeyValuePair<string, JsonNode>
                    (
                        g.Key,
                        serializationService.SerializeToArray(g.Value.Instances)
                    )
                )
        );

    public static JsonArray SerializeToArray(this ISerializationService serializationService, ImmutableDictionary<object, object> data) 
        => new(data.Select(serializationService.SerializeEntity).ToArray());


    public static JsonNode SerializeEntity(this ISerializationService serializationService, KeyValuePair<object,object> kvp)
    {
        if (kvp.Value is JsonObject node)
            return node;
        node = (JsonObject)JsonNode.Parse(JsonSerializer.Serialize(kvp.Value));
        node!.TryAdd(ReservedProperties.Id, serializationService.SerializeToString(kvp.Key));
        return node;
    }

    public static IEnumerable<EntityDescriptor> DeserializeToEntities(this ISerializationService serializationService, string collection, JsonArray array)
    {
        foreach (var item in array.OfType<JsonObject>())
        {
            if (item.TryGetPropertyValue(ReservedProperties.Id, out var id)
                && id != null
                && item.TryGetPropertyValue(ReservedProperties.DataSource, out var dataSource)
                && dataSource != null)
                yield return new(collection,                    serializationService.Deserialize(id.ToString()), serializationService.Deserialize(item.ToString()));
        }
    }


    public static IReadOnlyCollection<EntityDescriptor> ConvertToData(this ISerializationService serializationService, JsonArray array)
    {
        return array
            .Select(serializationService.DeserializeArrayElements).ToArray();
    }

    private static EntityDescriptor DeserializeArrayElements(this ISerializationService serializationService, JsonNode node)
    {
        if (node is not JsonObject obj
            || !obj.TryGetPropertyValue(ReservedProperties.Id, out var id)
            || !obj.TryGetPropertyValue(ReservedProperties.Type, out var collectionName)
            )
            return default;

        var deserialize = serializationService.Deserialize(obj.ToString());
        return new EntityDescriptor(
            collectionName!.ToString(),
            serializationService.Deserialize(id!.ToString()),
            deserialize
        );
    }

}