using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Newtonsoft.Json;
using OpenSmc.Messaging;
using OpenSmc.Serialization;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace OpenSmc.Data.Persistence;

public static class DataPersistenceExtensions
{
    public static JsonNode SerializeWorkspaceData(this ISerializationService serializationService, IReadOnlyDictionary<string, IReadOnlyDictionary<object, object>> data)
        => JsonNode.Parse(JsonSerializer.Serialize(serializationService.SerializeDataToDictionary(data)));

    private static ImmutableDictionary<string, JsonArray> SerializeDataToDictionary(this ISerializationService serializationService, IReadOnlyDictionary<string, IReadOnlyDictionary<object, object>> data)
        => data.Select
            (
                kvp =>
                    new KeyValuePair<string, JsonArray>
                    (
                        kvp.Key,
                        serializationService.SerializeEntities(kvp.Key, kvp.Value)
                    )
            )
            .ToImmutableDictionary();


    public static JsonArray SerializeEntities(this ISerializationService serializationService, string collection,
        IReadOnlyDictionary<object, object> instancesByKey)
        => new(instancesByKey.Select(kvp => serializationService.SerializeEntity(collection, kvp.Key, kvp.Value)).ToArray());
        

    private static JsonNode SerializeEntity(this ISerializationService serializationService, string collection,
        object id, object instance)
        => JsonNode.Parse(JsonSerializer.Serialize(serializationService.SerializeEntityToDictionary(collection, id, instance)));

    private static ImmutableDictionary<string, object> SerializeEntityToDictionary(this ISerializationService serializationService, string collection, object id, object instance)
        => JsonConvert.DeserializeObject<ImmutableDictionary<string, object>>(serializationService.Serialize(instance).Content)
            .SetItem(ReservedProperties.Id, id)
            .SetItem(ReservedProperties.Type, collection);



    public static ImmutableDictionary<object, object> ParseIdAndObject(this ISerializationService serializationService,
        JsonNode token)
        => ParseIdAndObjectImpl(serializationService, (dynamic)token);

    private static ImmutableDictionary<object, object> ParseIdAndObjectImpl(this ISerializationService serializationService, JsonArray array) 
        =>
        array.OfType<JsonObject>().Select(serializationService.ParseIdAndObjectOfSingleInstance)
            .Where(x => !Equals(x, default(KeyValuePair<object, object>)))
            .ToImmutableDictionary();

    private static ImmutableDictionary<object, object> ParseIdAndObjectImpl(this ISerializationService serializationService, JsonObject jsonObject)
    {
        var kvp = serializationService.ParseIdAndObjectOfSingleInstance(jsonObject);
        return kvp.Key == null ? ImmutableDictionary<object, object>.Empty : new[]{kvp}.ToImmutableDictionary();
    }
    private static ImmutableDictionary<object, object> ParseIdAndObjectImpl(this ISerializationService serializationService, JsonNode token) => throw new NotSupportedException();

    private static KeyValuePair<object, object> ParseIdAndObjectOfSingleInstance(this ISerializationService serializationService, JsonObject node) 
        =>
        node.TryGetPropertyValue(ReservedProperties.Id, out var id) && id != default
            ? new(serializationService.Deserialize(id.ToString()),
                serializationService.Deserialize(node.ToString()))
            : default;
}