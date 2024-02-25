using System.Text.Json.Nodes;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data.Persistence;

public static class DataPersistenceExtensions
{
    public static JsonObject SerializeState(this ISerializationService serializationService, IEnumerable<EntityDescriptor> data) 
        => new(
            data.GroupBy(e => e.Collection)
                .Select(g => new KeyValuePair<string, JsonNode>
                    (
                        g.Key,
                        serializationService.SerializeToArray(g)
                    )
                )
        );

    public static JsonArray SerializeToArray(this ISerializationService serializationService, IEnumerable<EntityDescriptor> data) 
        => new(data.Select(serializationService.SerializeEntity).ToArray());


    public static JsonNode SerializeEntity(this ISerializationService serializationService, EntityDescriptor entity)
    {
        var node = (JsonObject)JsonNode.Parse(serializationService.Serialize(entity.Entity).Content);
        node!.Add(ReservedProperties.Id, serializationService.Serialize(entity.Id).Content);
        node.Add(ReservedProperties.DataSource, serializationService.Serialize(entity.DataSource).Content);
        //node.Add(ReservedProperties.Type, entity.Collection);
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
                yield return new(serializationService.Deserialize(dataSource.ToString()), collection,
                    serializationService.Deserialize(id.ToString()), serializationService.Deserialize(item.ToString()));
        }
    }


    public static IEnumerable<EntityDescriptor> ConvertToData(this ISerializationService serializationService, JsonArray array)
    {
        return array
            .Select(serializationService.DeserializeArrayElements);
    }

    private static EntityDescriptor DeserializeArrayElements(this ISerializationService serializationService, JsonNode node)
    {
        if (node is not JsonObject obj
            || !obj.TryGetPropertyValue(ReservedProperties.Id, out var id)
            || !obj.TryGetPropertyValue(ReservedProperties.DataSource, out var dataSource)
            || !obj.TryGetPropertyValue(ReservedProperties.Type, out var collectionName)
            )
            return default;

        return new EntityDescriptor(
            serializationService.Deserialize(dataSource!.ToString()),
            collectionName!.ToString(),
            serializationService.Deserialize(id!.ToString()),
            serializationService.Deserialize(obj.ToString())
        );
    }

}

public record EntityDescriptor(object DataSource, string Collection, object Id, object Entity);
