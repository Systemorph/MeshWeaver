using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public record ReduceManager
{
    internal readonly LinkedList<Reduce> Reducers = new();
    internal readonly LinkedList<ReduceStream> ReduceStreams = new();

    public ReduceManager()
    {
        AddWorkspaceReference<EntityReference, object>((ws, reference) => ws.Store.ReduceImpl(reference))
            .AddWorkspaceReference<PartitionedCollectionsReference, EntityStore>((ws,reference) => ws.ReduceImpl(reference))
            .AddWorkspaceReference<CollectionReference, InstanceCollection>((ws, reference) => ws.Store.ReduceImpl(reference))
            .AddWorkspaceReference<CollectionsReference, EntityStore>((ws, reference) => ws.Store.ReduceImpl(reference))
            .AddWorkspaceReference<EntireWorkspace, EntityStore>((ws, reference) => ws.Store.ReduceImpl(reference));
    }

    public ReduceManager AddWorkspaceReferenceStream<TReference, TStream>(Func<IObservable<ChangeItem<WorkspaceState>>, TReference, IObservable<ChangeItem<TStream>>> reducer)
        where TReference : WorkspaceReference<TStream>
    {
        object Stream(IObservable<ChangeItem<WorkspaceState>> stream, WorkspaceReference reference, LinkedListNode<ReduceStream> node)
            => ReduceImpl(stream, reference, reducer,node);

        ReduceStreams.AddFirst(Stream);
        return this;
    }
    public ReduceManager AddWorkspaceReference<TReference, TStream>(Func<WorkspaceState, TReference, TStream> reducer)
        where TReference : WorkspaceReference<TStream>
    {
        object Lambda(WorkspaceState ws, WorkspaceReference r, LinkedListNode<Reduce> node) => ReduceImpl(ws, r, reducer, node);
        Reducers.AddFirst(Lambda);

        object Stream(IObservable<ChangeItem<WorkspaceState>> stream, WorkspaceReference reference, LinkedListNode<ReduceStream> node)
            => stream.Select(ws => Reduce<TStream>(ws, reference));

        ReduceStreams.AddFirst(Stream);
        return this;
    }

    private ChangeItem<TStream> Reduce<TStream>(ChangeItem<WorkspaceState> ws, WorkspaceReference reference)
    {
        return ws.SetValue((TStream)Reduce(ws.Value, reference));
    }

    private static object ReduceImpl<TReference, TStream>(IObservable<ChangeItem<WorkspaceState>> state, WorkspaceReference @ref,
        Func<IObservable<ChangeItem<WorkspaceState>>, TReference, IObservable<ChangeItem<TStream>>> reducer, LinkedListNode<ReduceStream> node)
        where TReference : WorkspaceReference<TStream>
    {
        return @ref is TReference reference
            ? reducer.Invoke(state, reference)
            : node.Next != null
                ? node.Next.Value.Invoke(state, @ref, node.Next)
                : throw new NotSupportedException($"Reducer for reference {@ref.GetType().Name} not specified");

    }

    private static object ReduceImpl<TReference, TStream>(WorkspaceState state, WorkspaceReference @ref, Func<WorkspaceState, TReference, TStream> reducer, LinkedListNode<Reduce> node) 
        where TReference : WorkspaceReference<TStream>
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
    public IObservable<ChangeItem<TReference>> ReduceStream<TReference>(IObservable<ChangeItem<WorkspaceState>> workspaceState, WorkspaceReference<TReference> reference)
    {
        var first = ReduceStreams.First;
        return ((IObservable<ChangeItem<TReference>>)first?.Value(workspaceState, reference, first));//!.Cast<ChangeItem<TReference>>();
    }
}

internal delegate object Reduce(WorkspaceState state, WorkspaceReference reference, LinkedListNode<Reduce> node);
internal delegate object ReduceStream(IObservable<ChangeItem<WorkspaceState>> state, WorkspaceReference reference, LinkedListNode<ReduceStream> node);

public record WorkspaceState
{

    private readonly Func<WorkspaceState, WorkspaceReference, object> reduce;

    //private readonly ISerializationService serializationService;
    private ImmutableDictionary<Type, string> CollectionsByType { get; init; }
    public ImmutableDictionary<string, ITypeSource> TypeSources { get; init; }
    public IMessageHub Hub { get; }
    public WorkspaceState
    (
        IMessageHub hub,
        EntityStore Store,
        IReadOnlyDictionary<string, ITypeSource> typeSources,
        Func<WorkspaceState, WorkspaceReference, object> reduce
    )
        : this(hub, typeSources, reduce)
    {
        Hub = hub;
        this.Store = Store;
    }

    public string GetCollectionName(Type type) => CollectionsByType.GetValueOrDefault(type);

    private WorkspaceState(IMessageHub hub, IReadOnlyDictionary<string, ITypeSource> typeSources,
        Func<WorkspaceState,WorkspaceReference, object> reduce)
    {
        this.reduce = reduce;
        Version = hub.Version;
        CollectionsByType = typeSources.Values.Where(x => x.ElementType != null)
            .ToImmutableDictionary(x => x.ElementType, x => x.CollectionName);
        TypeSources = typeSources.Values.ToImmutableDictionary(x => x.CollectionName);

    }



    public long Version { get; init; }


    #region Instances

    public EntityStore Store { get; private init; }




    #endregion

    #region Reducers


    public object Reduce(WorkspaceReference reference)
        => reduce(this, reference);


    public TReference Reduce<TReference>(WorkspaceReference<TReference> reference)
        => (TReference)reduce(this, reference);


    internal EntityStore ReduceImpl(PartitionedCollectionsReference reference) =>
        new()
        {
            Collections = ((EntityStore)Reduce((dynamic)reference.Collections))
                .Collections
                .Select(c =>
                    new KeyValuePair<string, InstanceCollection>(c.Key,
                        GetPartitionedCollection(c.Key, reference.Partition)))
                .Where(x => x.Value != null)
                .ToImmutableDictionary()
        };

    private InstanceCollection GetPartitionedCollection(string collection, object partition)
    {
        var ret = Store.GetCollection(collection);
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

        var patch = request.Change;
        var newState = patch.Apply(LastSynchronized);
        return this with
        {
            LastSynchronized = newState.Result,
            Store = newState.Result.Deserialize<EntityStore>(Hub.DeserializationOptions)
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
            LastSynchronized = JsonSerializer.SerializeToNode(newElements, Hub.SerializationOptions),
            Version = Hub.Version
        };

    }

    private EntityStore Merge(DataChangeRequestWithElements request) =>
        request switch
        {
            UpdateDataRequest update => Store with { Collections = Store.Collections.SetItems(MergeUpdate(update)) },
            DeleteDataRequest delete => Store with { Collections = Store.Collections.SetItems(MergeDelete(delete)) },

_ => throw new NotSupportedException()
        };

    private IEnumerable<KeyValuePair<string, InstanceCollection>> MergeDelete(DeleteDataRequest update)
    {
        var instances = GetChanges(update.Elements);
        foreach (var kvp in instances)
        {
            var collection = kvp.Key;
            var existing = Store.GetCollection(collection);
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
            var existing = Store.GetCollection(collection);
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
            yield return new(typeProvider.CollectionName, new(){Instances = g.ToImmutableDictionary(typeProvider.GetKey), GetKey = typeProvider.GetKey });
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
            LastSynchronized = JsonSerializer.SerializeToNode(newStore, Hub.SerializationOptions),
            Version = Hub.Version,
        };
    }

    private EntityStore CreateNewStore(ChangeItem<EntityStore> item) =>
        Store with
        {
            Collections = Store.Collections.SetItems(item.Value.Collections.Select(kvp =>
                new KeyValuePair<string, InstanceCollection>(kvp.Key,
                    TypeSources.TryGetValue(kvp.Key, out var ts) && ts is IPartitionedTypeSource &&
                    Store.Collections.TryGetValue(kvp.Key, out var existing)
                        ? existing.Merge(kvp.Value)
                        : kvp.Value)))

        };

    public WorkspaceState Update(string collection, Func<InstanceCollection, InstanceCollection> change)
        => this with { Store = Store.Update(collection, change) };
    public WorkspaceState Update(Func<EntityStore, EntityStore> change)
        => this with { Store = change(Store) };

    public WorkspaceState Update(object instance)
    {
        if(instance == null)
            throw new ArgumentNullException(nameof(instance));
        var type = instance.GetType();
        if (!CollectionsByType.TryGetValue(type, out var collection))
            throw new InvalidOperationException($"Type {type.FullName} is not mapped to data source.");
        var typeProvider = TypeSources.GetValueOrDefault(collection);
        if (typeProvider == null)
            throw new InvalidOperationException($"Type {type.FullName} is not mapped to data source.");
        return Update(collection, c => c.Update(typeProvider.GetKey(instance), instance));
    }


}