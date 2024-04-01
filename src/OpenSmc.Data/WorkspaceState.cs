using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public record ReduceManager
{
    internal readonly LinkedList<Reduce> Reducers = new();
    internal readonly LinkedList<ReduceStream> ReduceStreams = new();

    public ReduceManager()
    {
        AddWorkspaceReference<EntityReference>((ws, reference) => ws.ReduceImpl(reference))
            .AddWorkspaceReference<PartitionedCollectionsReference>((ws,reference) => ws.ReduceImpl(reference))
            .AddWorkspaceReference<CollectionReference>((ws, reference) => ws.ReduceImpl(reference))
            .AddWorkspaceReference<CollectionsReference>((ws, reference) => ws.ReduceImpl(reference))
            .AddWorkspaceReference<EntireWorkspace>((ws, reference) => ws.ReduceImpl(reference));
    }

    public ReduceManager AddWorkspaceReferenceStream<TReference>(Func<IObservable<WorkspaceState>, TReference, IObservable<object>> reducer)
        where TReference : WorkspaceReference
    {
        IObservable<object> Stream(IObservable<WorkspaceState> stream, WorkspaceReference reference,
            LinkedListNode<ReduceStream> node)
            => ReduceImpl(stream, reference, reducer,node);

        ReduceStreams.AddFirst(Stream);
        return this;
    }
    public ReduceManager AddWorkspaceReference<TReference>(Func<WorkspaceState, TReference, object> reducer)
        where TReference : WorkspaceReference
    {
        object Lambda(WorkspaceState ws, WorkspaceReference r, LinkedListNode<Reduce> node) => ReduceImpl(ws, r, reducer, node);
        Reducers.AddFirst(Lambda);

        IObservable<object> Stream(IObservable<WorkspaceState> stream, WorkspaceReference reference,
            LinkedListNode<ReduceStream> node)
            => stream.Select(ws => Reduce(ws, reference));

        ReduceStreams.AddFirst(Stream);
        return this;
    }

    private static IObservable<object> ReduceImpl<TReference>(IObservable<WorkspaceState> state, WorkspaceReference @ref,
        Func<IObservable<WorkspaceState>, TReference, IObservable<object>> reducer, LinkedListNode<ReduceStream> node)
        where TReference : WorkspaceReference
    {
        return @ref is TReference reference
            ? reducer.Invoke(state, reference)
            : node.Next != null
                ? node.Next.Value.Invoke(state, @ref, node.Next)
                : throw new NotSupportedException($"Reducer for reference {@ref.GetType().Name} not specified");

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
        return first!.Value(workspaceState, reference, first);
    }
    public IObservable<TReference> ReduceStream<TReference>(IObservable<WorkspaceState> workspaceState, WorkspaceReference<TReference> reference)
    {
        var first = ReduceStreams.First;
        return first?.Value(workspaceState, reference, first)?.Cast<TReference>();
    }
}

internal delegate object Reduce(WorkspaceState state, WorkspaceReference reference, LinkedListNode<Reduce> node);
internal delegate IObservable<object> ReduceStream(IObservable<WorkspaceState> state, WorkspaceReference reference, LinkedListNode<ReduceStream> node);
public record WorkspaceState
{
    private readonly IMessageHub hub;

    private readonly ReduceManager reduceManager;
    //private readonly ISerializationService serializationService;
    private ImmutableDictionary<Type, string> CollectionsByType { get; init; }
    public ImmutableDictionary<string, ITypeSource> TypeSources { get; init; }
    public WorkspaceState
    (
        IMessageHub hub,
        EntityStore Store,
        IReadOnlyDictionary<string, ITypeSource> typeSources,
        ReduceManager reduceManager
    )
        : this(hub, typeSources, reduceManager)
    {
        this.hub = hub;
        this.Store = Store;
    }

    private WorkspaceState(IMessageHub hub, IReadOnlyDictionary<string, ITypeSource> typeSources, ReduceManager reduceManager1)
    {
        this.reduceManager = reduceManager1;
        Version = hub.Version;
        var serializationService = hub.ServiceProvider.GetRequiredService<ISerializationService>();
        CollectionsByType = typeSources.Values.Where(x => x.ElementType != null).ToImmutableDictionary(x => x.ElementType, x => x.CollectionName);
        TypeSources = typeSources.Values.ToImmutableDictionary(x => x.CollectionName);
        Options = hub.JsonSerializerOptions;

    }

    private JsonSerializerOptions Options { get;  }


    public long Version { get; init; }


    #region Instances
    public EntityStore Store { get; private init; }

    public InstanceCollection GetCollection(string collection) => Store.Instances.GetValueOrDefault(collection);



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

    internal InstanceCollection ReduceImpl(CollectionReference reference) =>
        GetCollection(reference.Collection);

    internal EntityStore ReduceImpl(CollectionsReference reference) =>
        new(reference
            .Collections
            .Select(c => new KeyValuePair<string, InstanceCollection>(c, GetCollection(c)))
            .Where(x => x.Value != null)
            .ToImmutableDictionary());

    internal EntityStore ReduceImpl(PartitionedCollectionsReference reference) =>
        new(Reduce(reference.Collections)
            .Instances
            .Select(c => new KeyValuePair<string, InstanceCollection>(c.Key, GetPartitionedCollection(c.Key, reference.Partition)))
            .Where(x => x.Value != null)
            .ToImmutableDictionary());

    private InstanceCollection GetPartitionedCollection(string collection, object partition)
    {
        var ret = GetCollection(collection);
        if (ret == null)
            return null;
        if (TypeSources.TryGetValue(collection, out var ts) && partition != null &&
            ts is IPartitionedTypeSource partitionedTypeSource)
            ret = ret with
            {
                Instances = ret.Instances
                    .Where(kvp => partition.Equals(partitionedTypeSource.GetPartition(kvp.Value)))
                    .ToImmutableDictionary()
            };
        return ret;
    }

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



    protected virtual WorkspaceState Change(DataChangeRequestWithElements request)
    {
        if (request.Elements == null)
            return null;

        var newElements = Merge(request);

        return this with
        {
            Store = newElements,
            LastSynchronized = JsonSerializer.SerializeToNode(newElements, Options),
            Version = hub.Version
        };

    }

    private EntityStore Merge(DataChangeRequestWithElements request) =>
        request switch
        {
            UpdateDataRequest update => new EntityStore(Store.Instances.SetItems(MergeUpdate(update))),
            DeleteDataRequest delete => new EntityStore(Store.Instances.SetItems(MergeDelete(delete))),
            _ => throw new NotSupportedException()
        };

    private IEnumerable<KeyValuePair<string, InstanceCollection>> MergeDelete(DeleteDataRequest update)
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

    private IEnumerable<KeyValuePair<string, InstanceCollection>> MergeUpdate(UpdateDataRequest update)
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

    private IEnumerable<KeyValuePair<string, InstanceCollection>> GetChanges(IReadOnlyCollection<object> instances)
    {
        foreach (var g in instances.GroupBy(x => x.GetType()))
        {
            var collection = CollectionsByType.GetValueOrDefault(g.Key);
            if(collection == null)
                throw new InvalidOperationException($"Type {g.Key.FullName} is not mapped to data source.");
            var typeProvider = TypeSources.GetValueOrDefault(collection);
            if (typeProvider == null)
                throw new InvalidOperationException($"Type {g.Key.FullName} is not mapped to data source.");
            yield return new(typeProvider.CollectionName, new(g.ToImmutableDictionary(typeProvider.GetKey))
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


    public WorkspaceState Synchronize(ChangeItem<EntityStore> item)
    {
        var newStore = CreateNewStore(item);
        return this with
        {
            Store = newStore,
            LastSynchronized = JsonSerializer.SerializeToNode(newStore, Options),
            Version = hub.Version,
        };
    }

    private EntityStore CreateNewStore(ChangeItem<EntityStore> item)
    {
        return new(
            Store.Instances.SetItems(item.Value.Instances.Select(kvp =>
                new KeyValuePair<string, InstanceCollection>(kvp.Key,
                    TypeSources.TryGetValue(kvp.Key, out var ts) && ts is IPartitionedTypeSource &&
                    Store.Instances.TryGetValue(kvp.Key, out var existing)
                        ? existing.Merge(kvp.Value)
                        : kvp.Value)))
        );
    }
}