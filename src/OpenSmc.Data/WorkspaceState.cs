using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public record ReduceManager
{
    internal readonly LinkedList<Reduce> Reducers = new();

    public ReduceManager()
    {
        WithReference<EntityReference>((ws, reference) => ws.ReduceImpl(reference))
            .WithReference<CollectionReference>((ws, reference) => ws.ReduceImpl(reference))
            .WithReference<CollectionsReference>((ws, reference) => ws.ReduceImpl(reference))
            .WithReference<EntireWorkspace>((ws, reference) => ws.ReduceImpl(reference));
    }

    public ReduceManager WithReference<TReference>(Func<WorkspaceState, TReference, object> reducer)
        where TReference : WorkspaceReference
    {
        Reducers.AddLast((ws, r, node) => ReduceImpl<TReference>(ws, r, reducer, node));
        return this;
    }

    private static object ReduceImpl<TReference>(WorkspaceState state, WorkspaceReference @ref, Func<WorkspaceState, TReference, object> reducer, LinkedListNode<Reduce> node) where TReference : WorkspaceReference
    {
        return @ref is TReference reference ?
         reducer.Invoke(state, reference)
         : node.Next  != null
             ? node.Next.Value.Invoke(state, @ref, node.Next) 
             : throw new NotSupportedException($"Reducer for reference {@ref.GetType().Name} not specified");
    }

    public object Reduce(WorkspaceState workspaceState, WorkspaceReference reference)
    {
        var first = Reducers.First;
        return first!.Value(workspaceState,reference, first);
    }
}

internal delegate object Reduce(WorkspaceState state, WorkspaceReference reference, LinkedListNode<Reduce> node);
public record WorkspaceState
{
    private readonly ReduceManager reduceManager;
    //private readonly ISerializationService serializationService;
    private ImmutableDictionary<Type, string> CollectionsByType { get; init; }
    public ImmutableDictionary<string, ITypeSource> TypeSources { get; init; }
    public object LastChangedBy { get; init; }
    public WorkspaceState
    (
        IMessageHub hub,
        EntityStore Store,
        IReadOnlyDictionary<string, ITypeSource> typeSources,
        ReduceManager reduceManager
    )
        : this(hub, typeSources, reduceManager)
    {
        this.Store = Store;
        LastSynchronized = JsonSerializer.SerializeToNode(Store, Options);
    }

    private WorkspaceState(IMessageHub hub, IReadOnlyDictionary<string, ITypeSource> typeSources, ReduceManager reduceManager1)
    {
        this.reduceManager = reduceManager1;
        Version = hub.Version;
        var serializationService = hub.ServiceProvider.GetRequiredService<ISerializationService>();
        CollectionsByType = typeSources.Values.Where(x => x.ElementType != null).ToImmutableDictionary(x => x.ElementType, x => x.CollectionName);
        TypeSources = typeSources.Values.ToImmutableDictionary(x => x.CollectionName);
        Options = serializationService.Options(TypeSources);

    }

    private JsonSerializerOptions Options { get;  }


    public WorkspaceState(IMessageHub hub, 
        DataChangedEvent dataChanged,
        IReadOnlyDictionary<string, ITypeSource> typeSources,
        ReduceManager reduceManager)
        : this(hub, typeSources, reduceManager)
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
            LastChangedBy = @event.Requester,
            Store = workspace.Deserialize<EntityStore>(Options)
        };
    }

    public long Version { get; init; }


    #region Instances
    public EntityStore Store { get; private init; }

    public InstancesInCollection GetCollection(string collection) => Store.Instances.GetValueOrDefault(collection);



    #endregion

    #region Reducers


    public object Reduce(WorkspaceReference reference)
        => reduceManager.Reduce(this, reference);


    public TReference Reduce<TReference>(WorkspaceReference<TReference> reference)
        => (TReference)reduceManager.Reduce(this, reference);


    internal object ReduceImpl(WorkspaceReference reference)
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

    internal object ReduceImpl(EntityReference reference) => GetCollection(reference.Collection)?.GetData(reference.Id);
    internal object ReduceImpl(EntireWorkspace _) => Store;

    internal InstancesInCollection ReduceImpl(CollectionReference reference) => 
        GetCollection(reference.Collection);

    internal EntityStore ReduceImpl(CollectionsReference reference) =>
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


    //private WorkspaceState ApplyPatch(JsonPatch patch)
    //{
    //    var newStoreSerialized = patch.Apply(LastSynchronized);
    //    if (newStoreSerialized.IsSuccess && newStoreSerialized.Result != null)
    //    {
    //        var newStore = (EntityStore)serializationService.Deserialize(newStoreSerialized.Result.ToJsonString());
    //        return this with
    //        {
    //            Store = newStore,
    //            LastSynchronized = newStoreSerialized.Result
    //        };
    //    }

    //    // TODO V10: Add error handling (11.03.2024, Roland Bürgi)

    //    return this;
    //}



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
            UpdateDataRequest update => new EntityStore(Store.Instances.SetItems(MergeUpdate(update))),
            DeleteDataRequest delete => new EntityStore(Store.Instances.SetItems(MergeDelete(delete))),
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
            else yield return new(kvp.Key, existing with{Instances = existing.Instances.SetItems(kvp.Value.Instances)});
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
    {
        return this with
        {
            CollectionsByType = CollectionsByType.SetItems(other.CollectionsByType),
            LastSynchronized = null,
            Store = new(Store.Instances.SetItems(other.Store.Instances)),
            TypeSources = TypeSources.SetItems(other.TypeSources)
        };
    }
}