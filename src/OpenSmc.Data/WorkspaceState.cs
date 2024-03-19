using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.More;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public static class WorkspaceJsonSerializer
{
    public static JsonSerializerOptions Options(this ISerializationService serializationService,
        IReadOnlyDictionary<string, ITypeSource> typeProviders) =>
        new()
        {
            Converters =
                { new EntityStoreConverter(typeProviders, serializationService), new SerializationServiceConverter(serializationService) }
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

public class EntityStoreConverter(IReadOnlyDictionary<string, ITypeSource> typeSourcesByCollection, ISerializationService serializationService) : JsonConverter<EntityStore>
{
    public override EntityStore Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        return Deserialize(doc.RootElement.AsNode(), options);
    }

    public override void Write(Utf8JsonWriter writer, EntityStore value, JsonSerializerOptions options)
    {
        Serialize(value).WriteTo(writer);
    }

    private JsonNode Serialize(EntityStore store)
    {
        var ret = new JsonObject(
            store.Instances.ToDictionary(
                x => x.Key,
                x => (JsonNode)new JsonArray(
                    x.Value.Instances.Select(i => SerializeInstance(x.Key, i.Key, i.Value)).ToArray()
                )
            )
        );
        return ret;
    }

    private JsonNode SerializeInstance(string collection, object id, object instance)
    {
        var ret = JsonSerializer.SerializeToNode(instance);
        ret![ReservedProperties.Type] = collection;
        ret[ReservedProperties.Id] = JsonSerializer.SerializeToNode(id);
        return ret;
    }

    public EntityStore Deserialize(JsonNode serializedWorkspace, JsonSerializerOptions options)
    {
        if (serializedWorkspace is not JsonObject obj)
            throw new ArgumentException("Invalid serialized workspace");

        var newStore = new EntityStore(obj.Select(kvp => DeserializeCollection(kvp.Key, kvp.Value, options)).ToImmutableDictionary());

        return newStore;
    }

    private KeyValuePair<string, InstancesInCollection> DeserializeCollection(string collection, JsonNode node, JsonSerializerOptions options)
    {
        return
            new(
                collection,
                DeserializeToInstances(node, typeSourcesByCollection.GetValueOrDefault(collection)?.ElementType, options)
            );
    }

    public InstancesInCollection DeserializeToInstances(JsonNode node, Type elementType, JsonSerializerOptions options)
    {
        if (node is not JsonArray array)
            throw new ArgumentException("Expecting an array");
        return new(array.Select(jsonNode => DeserializeEntity(jsonNode, elementType, options)).ToImmutableDictionary());
    }

    private KeyValuePair<object, object> DeserializeEntity(JsonNode jsonNode, Type elementType, JsonSerializerOptions options)
    {
        if (jsonNode is not JsonObject obj)
            throw new ArgumentException($"Expecting node type to be object");
        if (!obj.TryGetPropertyValue(ReservedProperties.Id, out var id) || id == null)
            throw new ArgumentException($"Expecting property {ReservedProperties.Id} to be set");
        return new(serializationService.Deserialize(id.ToJsonString()), elementType == null ? jsonNode.DeepClone() : jsonNode.Deserialize(elementType));
    }



}

public record WorkspaceState
{
    private readonly ISerializationService serializationService;
    private ImmutableDictionary<Type, string> CollectionsByType { get; init; }
    public ImmutableDictionary<string, ITypeSource> TypeSources { get; init; }
    private IMessageHub Hub { get; }
    public WorkspaceState
    (
        IMessageHub hub,
        EntityStore Store,
        IReadOnlyDictionary<string, ITypeSource> typeSources
    )
        : this(hub, typeSources)
    {
        Hub = hub;
        this.Store = Store;
        LastSynchronized = JsonSerializer.SerializeToNode(Store, Options);
    }
    public WorkspaceState
    (
        IMessageHub hub,
        JsonNode LastSynchronized,
        IReadOnlyDictionary<string, ITypeSource> typeSources
    )
        : this(hub, typeSources)
    {
        Hub = hub;
        this.LastSynchronized = LastSynchronized;
        Store = LastSynchronized.Deserialize<EntityStore>(Options);
    }

    private WorkspaceState(IMessageHub hub, IReadOnlyDictionary<string, ITypeSource> typeSources)
    {
        Hub = hub;
        Version = hub.Version;
        serializationService = hub.ServiceProvider.GetRequiredService<ISerializationService>();
        CollectionsByType = typeSources.Values.Where(x => x.ElementType != null).ToImmutableDictionary(x => x.ElementType, x => x.CollectionName);
        TypeSources = typeSources.Values.ToImmutableDictionary(x => x.CollectionName);
        Options = serializationService.Options(TypeSources);
    }

    private JsonSerializerOptions Options { get;  }


    public WorkspaceState(IMessageHub hub, DataChangedEvent dataChanged,
        IReadOnlyDictionary<string, ITypeSource> typeSources)
        : this(hub, typeSources)
    {
        LastSynchronized = GetSerializedWorkspace(dataChanged);
        Store = LastSynchronized.Deserialize<EntityStore>(Options);
    }
    private JsonNode GetSerializedWorkspace(DataChangedEvent node) =>
        node.ChangeType switch
        {
            ChangeType.Full => JsonNode.Parse(node.Change.Content),
            ChangeType.Patch => JsonSerializer.Deserialize<JsonPatch>(node.Change.Content)
                .Apply(LastSynchronized)
                .Result,
            _ => throw new ArgumentOutOfRangeException()
        };


    public WorkspaceState Synchronize(DataChangedEvent @event)
    {
        var workspace = GetSerializedWorkspace(@event);
        return this with
        {
            LastSynchronized = workspace,
            Store = workspace.Deserialize<EntityStore>(Options)
        };
    }

    public long Version { get; init; }


    #region Instances
    public EntityStore Store { get; private init; }

    public InstancesInCollection GetCollection(string collection) => Store.Instances.GetValueOrDefault(collection);

    public WorkspaceState SetItems(EntityStore store)
    {
        var newStore = new EntityStore(Store.Instances.SetItems
            (
                store.Instances.Select
                (
                    change =>
                        new KeyValuePair<string, InstancesInCollection>
                        (
                            change.Key, change.Value.Merge(Store.Instances.GetValueOrDefault(change.Key))
                        )

                )
            )
        );
        return this with
        {
            Store = newStore,
        };
    }


    #endregion

    #region Reducers



    public object Reduce(WorkspaceReference reference)
        => ReduceImpl((dynamic)reference);

    public TReference Reduce<TReference>(WorkspaceReference<TReference> reference)
        => (TReference)ReduceImpl((dynamic)reference);


    private object ReduceImpl(WorkspaceReference reference)
    {
        throw new NotSupportedException($"Reducing with type {reference.GetType().FullName} is not supported.");
    }

    //private JsonNode ReduceImpl(JsonPathReference reducer)
    //{
    //    var node = GetCurrentJsonNode();

    //    var jsonPath = JsonPath.Parse(reducer.Path);
    //    var evaluated = jsonPath.Evaluate(node);
    //    var match = evaluated.Matches switch
    //    {
    //        { Count: 1 } => evaluated.Matches[0].Value,
    //        { Count: > 1 } => new JsonArray(evaluated.Matches.Select(x => x.Value).ToArray()),
    //        _ => null
    //    };
    //    return match;
    //}

    private object ReduceImpl(EntityReference reference) => GetCollection(reference.Collection)?.GetData(reference.Id);
    private object ReduceImpl(EntireWorkspace _) => Store;

    private object ReduceImpl<T>(EntityReference reference)
    {
        if (!CollectionsByType.TryGetValue(typeof(T), out var collection))
            return null;
        return GetCollection(collection)?.GetData(reference.Id);
    }
    private InstancesInCollection ReduceImpl(CollectionReference reference) => 
        reference.Transformation(GetCollection(reference.Collection));

    private EntityStore ReduceImpl(CollectionsReference reference) =>
        new(reference
            .Collections
            .Select(c => new KeyValuePair<string, InstancesInCollection>(c, GetCollection(c)))
            .Where(x => x.Value != null)
            .ToImmutableDictionary());



    #endregion


    public WorkspaceState Change(DataChangeRequest request)
        => request switch
        {
            DataChangeRequestWithElements requestWithElements => Change(requestWithElements),
            PatchChangeRequest patch => Change(patch),
            _ => throw new ArgumentOutOfRangeException($"No implementation found for {request.GetType().FullName}")

        };


    public WorkspaceState Change(PatchChangeRequest request)
    {
        if (LastSynchronized == null)
            throw new ArgumentException("Cannot patch workspace which has not been initialized.");

        var patch = (JsonPatch)request.Change;
        var newState = patch.Apply(LastSynchronized);
        return this with
        {
            LastSynchronized = newState.Result,
            Store = newState.Result.Deserialize<EntityStore>(Options)
        };
    }

    private JsonNode LastSynchronized { get; init; }


    private WorkspaceState ApplyPatch(JsonPatch patch)
    {
        var newStoreSerialized = patch.Apply(LastSynchronized);
        if (newStoreSerialized.IsSuccess && newStoreSerialized.Result != null)
        {
            var newStore = (EntityStore)serializationService.Deserialize(newStoreSerialized.Result.ToJsonString());
            return this with
            {
                Store = newStore,
                LastSynchronized = newStoreSerialized.Result
            };
        }

        // TODO V10: Add error handling (11.03.2024, Roland Bürgi)

        return this;
    }



    protected virtual WorkspaceState Change(DataChangeRequestWithElements request)
    {
        if (request.Elements == null)
            return null;

        var newElements = Merge(request);

        return this with
        {
            Store = newElements,
            LastSynchronized = JsonSerializer.SerializeToNode(newElements, Options)
        };

    }

    private EntityStore Merge(DataChangeRequestWithElements request) =>
        request switch
        {
            UpdateDataRequest update => new EntityStore(MergeUpdate(update).ToImmutableDictionary()),
            DeleteDataRequest delete => new EntityStore(MergeDelete(delete).ToImmutableDictionary()),
            _ => throw new NotSupportedException()
        };

    private IEnumerable<KeyValuePair<string, InstancesInCollection>> MergeDelete(DeleteDataRequest update)
    {
        var instances = GetChanges(update.Elements);
        foreach (var kvp in instances)
        {
            var collection = kvp.Key;
            var existing = GetCollection(collection);
            if (existing != null)
               yield return new(kvp.Key, kvp.Value with{Instances = existing.Instances.RemoveRange(kvp.Value.Instances.Keys) });
        }
    }

    private IEnumerable<KeyValuePair<string, InstancesInCollection>> MergeUpdate(UpdateDataRequest update)
    {
        var instances = GetChanges(update.Elements);
        foreach (var kvp in instances)
        {
            var collection = kvp.Key;
            var existing = GetCollection(collection);
            bool snapshotMode = update.Options?.Snapshot ?? false;
            if (existing == null || snapshotMode)
                yield return kvp;
            else yield return new(kvp.Key, kvp.Value.Merge(existing));
        }
    }

    private IEnumerable<KeyValuePair<string, InstancesInCollection>> GetChanges(IReadOnlyCollection<object> instances)
    {
        foreach (var g in instances.GroupBy(x => x.GetType()))
        {
            var collection = CollectionsByType.GetValueOrDefault(g.Key);
            if(collection == null)
                throw new InvalidOperationException($"Type {g.Key.FullName} is not mapped to data source.");
            var typeProvider = TypeSources.GetValueOrDefault(collection);
            if (typeProvider == null)
                throw new InvalidOperationException($"Type {g.Key.FullName} is not mapped to data source.");
            yield return new(typeProvider.CollectionName, new(instances.ToImmutableDictionary(typeProvider.GetKey))
            {
                GetKey = typeProvider.GetKey
            });
        }
    }

    public IEnumerable<Type> MappedTypes => CollectionsByType.Keys;


    public void Rollback()
    {
        throw new NotImplementedException();
    }


    public WorkspaceState Merge(WorkspaceState other)
        => SetItems(other.Store) with
        {
            TypeSources = TypeSources.SetItems(other.TypeSources),
            CollectionsByType = CollectionsByType.SetItems(other.CollectionsByType),
        };
}