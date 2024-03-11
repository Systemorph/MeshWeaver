using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public record WorkspaceState
{
    private readonly ISerializationService serializationService;
    private ImmutableDictionary<Type, string> CollectionsByType { get; init; }
    private ImmutableDictionary<Type, ITypeSource> TypeSourcesByType { get; init; }
    private ImmutableDictionary<string, Type> TypeSourcesByCollection { get; init; }
    private IMessageHub Hub { get; }
    public WorkspaceState
        (
            IMessageHub hub,     
            EntityStore Store, 
            IReadOnlyDictionary<Type, ITypeSource> typeSources
            )
        :this(hub, typeSources)
    {
        Hub = hub;
        this.Store = Store;
        TypeSourcesByType = typeSources.ToImmutableDictionary();
        CollectionsByType = this.Store.Instances.Where(x => x.Value.ElementType != null).ToImmutableDictionary(x => x.Value.ElementType, x => x.Key);
        TypeSourcesByCollection = CollectionsByType.ToImmutableDictionary(x => x.Value, x => x.Key);
    }

    private WorkspaceState(IMessageHub hub, IReadOnlyDictionary<Type, ITypeSource> typeSources)
    {
        Hub = hub;
        Version = hub.Version;
        serializationService = hub.ServiceProvider.GetRequiredService<ISerializationService>();
        CollectionsByType = typeSources.ToImmutableDictionary(x => x.Key, x => x.Value.CollectionName);
        TypeSourcesByCollection = typeSources.Values.ToImmutableDictionary(x => x.CollectionName, x => x.ElementType);
        TypeSourcesByType = typeSources.ToImmutableDictionary();
    }


    public WorkspaceState(IMessageHub hub, DataChangedEvent dataChanged,
        IReadOnlyDictionary<Type, ITypeSource> typeSources)
        : this(hub, (EntityStore)dataChanged.Change, typeSources)
    {
        LastSynchronized = this;
    }

    public long Version { get; init; }


    #region Instances
    public EntityStore Store { get; init; }

    public InstancesInCollection GetCollection(string collection) => Store.Instances.GetValueOrDefault(collection);

    public WorkspaceState SetItems(EntityStore store)
    {
        return this with
        {
            Store = new EntityStore(Instances: Store.Instances
                .SetItems
                (
                    store.Instances.Select
                    (
                        change =>
                            new KeyValuePair<string, InstancesInCollection>
                            (
                                change.Key, change.Value.Merge(Store.Instances.GetValueOrDefault(change.Key))
                            ))))

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

    private object ReduceImpl<T>(EntityReference<T> reference)
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
            .Select(c => new KeyValuePair<string,InstancesInCollection>(c, GetCollection(c)))
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



    public WorkspaceState Synchronize(DataChangedEvent @event)
    {
        if (@event.Change == null)
            return this;

        var newWorkspace = @event.Change;
        if (newWorkspace == null)
            throw new NotSupportedException();

        var newInstances =
            newWorkspace switch
            {
                WorkspaceState store => store,
                JsonPatch patch => ApplyPatch(patch),
                JsonNode o => ApplyPatch(o.Deserialize<JsonPatch>()),
                _ => throw new NotSupportedException()
            };

        return this with
        {
            Version = Hub.Version,
            Store = newInstances.Store,
            LastSynchronized = newInstances.LastSynchronized
        };
    }

    public WorkspaceState LastSynchronized { get; init; }

    private WorkspaceState ApplyPatch(JsonPatch patch)
    {
        var current = JsonNode.Parse(serializationService.SerializeToString(Store));
        var result = patch.Apply(current);
        if (result.IsSuccess && result.Result != null)
        {
            var newStore = (WorkspaceState)serializationService.Deserialize(result.Result.ToJsonString());
            return this with
            {
                Store = newStore.Store,
                LastSynchronized = newStore
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

        // TODO V10: It's easier to split data away from state (11.03.2024, Roland Bürgi)
        var ret = this with
        {
            Store = newElements
        };
        return ret with
        {
            Store = newElements,
            LastSynchronized = ret
        };

    }

    private EntityStore Merge(DataChangeRequestWithElements request)
    {
        switch (request)
        {
            case UpdateDataRequest update:
                return new(MergeUpdate(update).ToImmutableDictionary());
            case DeleteDataRequest delete:
                return new(MergeDelete(delete).ToImmutableDictionary());
        }

        throw new NotSupportedException();
    }

    private IEnumerable<KeyValuePair<string, InstancesInCollection>> MergeDelete(DeleteDataRequest update)
    {
        var instances = GetChanges(update.Elements);
        foreach (var kvp in instances)
        {
            var collection = kvp.Key;
            var existing = GetCollection(collection);
            if (existing != null)
               yield return new(kvp.Key, kvp.Value with{Instances = existing.Instances.Remove(kvp.Value.Instances.Keys)});
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
            var typeProvider = TypeSourcesByType.GetValueOrDefault(g.Key);
            if (typeProvider != null)
                yield return new(typeProvider.CollectionName, new(instances.ToImmutableDictionary(typeProvider.GetKey)));
            
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
            TypeSourcesByType = TypeSourcesByType.SetItems(other.TypeSourcesByType),
            TypeSourcesByCollection = TypeSourcesByCollection.SetItems(other.TypeSourcesByCollection),
            CollectionsByType = CollectionsByType.SetItems(other.CollectionsByType),
        };
}