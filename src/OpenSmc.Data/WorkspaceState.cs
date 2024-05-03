using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public record WorkspaceState
{
    private readonly IReduceManager<WorkspaceState> reduce;

    //private readonly ISerializationService serializationService;
    private ImmutableDictionary<Type, string> CollectionsByType { get; init; }
    public ImmutableDictionary<string, ITypeSource> TypeSources { get; init; }
    public IMessageHub Hub { get; }

    public WorkspaceState(
        IMessageHub hub,
        EntityStore Store,
        IReadOnlyDictionary<string, ITypeSource> typeSources,
        IReduceManager<WorkspaceState> reduce
    )
    {
        Hub = hub;
        this.Store = Store;
        this.reduce = reduce;
        Version = hub.Version;
        CollectionsByType = typeSources
            .Values.Where(x => x.ElementType != null)
            .ToImmutableDictionary(x => x.ElementType, x => x.CollectionName);
        TypeSources = typeSources.Values.ToImmutableDictionary(x => x.CollectionName);
    }

    public string GetCollectionName(Type type) => CollectionsByType.GetValueOrDefault(type);

    public WorkspaceState(
        IMessageHub hub,
        IReadOnlyDictionary<string, ITypeSource> typeSources,
        IReduceManager<WorkspaceState> reduce
    )
        : this(hub, new(), typeSources, reduce) { }

    public long Version { get; init; }

    public EntityStore Store { get; private init; }

    #region Reducers


    public object Reduce(WorkspaceReference reference) => reduce.Reduce(this, reference);

    public TReference Reduce<TReference>(WorkspaceReference<TReference> reference) =>
        reduce.Reduce(this, reference);

    internal EntityStore ReduceImpl(PartitionedCollectionsReference reference) =>
        new()
        {
            Collections = ((EntityStore)Reduce((dynamic)reference.Collections))
                .Collections.Select(c => new KeyValuePair<string, InstanceCollection>(
                    c.Key,
                    GetPartitionedCollection(c.Key, reference.Partition)
                ))
                .Where(x => x.Value != null)
                .ToImmutableDictionary()
        };

    private InstanceCollection GetPartitionedCollection(string collection, object partition)
    {
        var ret = Store.GetCollection(collection);
        if (ret == null)
            return null;
        if (
            TypeSources.TryGetValue(collection, out var ts)
            && partition != null
            && ts is IPartitionedTypeSource partitionedTypeSource
        )
            ret = ret with
            {
                Instances = ret
                    .Instances.Where(kvp =>
                        partition.Equals(partitionedTypeSource.GetPartition(kvp.Value))
                    )
                    .ToImmutableDictionary()
            };
        return ret;
    }

    #endregion

    public WorkspaceState Update(IReadOnlyCollection<object> instances, UpdateOptions options) =>
        Change(new UpdateDataRequest(instances) { Options = options });

    public WorkspaceState Change(DataChangeRequest request) =>
        request switch
        {
            DataChangeRequestWithElements requestWithElements => Change(requestWithElements),
            PatchChangeRequest patch => Change(patch),
            _
                => throw new ArgumentOutOfRangeException(
                    $"No implementation found for {request.GetType().FullName}"
                )
        };

    protected virtual WorkspaceState Change(DataChangeRequestWithElements request)
    {
        if (request.Elements == null)
            return null;

        var newElements = Merge(request);

        return this with
        {
            Store = newElements,
            Version = Hub.Version
        };
    }

    private EntityStore Merge(DataChangeRequestWithElements request) =>
        request switch
        {
            UpdateDataRequest update
                => Store with
                {
                    Collections = Store.Collections.SetItems(MergeUpdate(update))
                },
            DeleteDataRequest delete
                => Store with
                {
                    Collections = Store.Collections.SetItems(MergeDelete(delete))
                },

            _ => throw new NotSupportedException()
        };

    private IEnumerable<KeyValuePair<string, InstanceCollection>> MergeDelete(
        DeleteDataRequest update
    )
    {
        var instances = GetChanges(update.Elements);
        foreach (var kvp in instances)
        {
            var collection = kvp.Key;
            var existing = Store.GetCollection(collection);
            if (existing != null)
                yield return new(
                    kvp.Key,
                    kvp.Value with
                    {
                        Instances = existing.Instances.RemoveRange(kvp.Value.Instances.Keys)
                    }
                );
        }
    }

    private IEnumerable<KeyValuePair<string, InstanceCollection>> MergeUpdate(
        UpdateDataRequest update
    )
    {
        var instances = GetChanges(update.Elements);
        foreach (var kvp in instances)
        {
            var collection = kvp.Key;
            var existing = Store.GetCollection(collection);
            bool snapshotMode = update.Options?.Snapshot ?? false;
            if (existing == null || snapshotMode)
                yield return kvp;
            else
                yield return new(
                    kvp.Key,
                    existing with
                    {
                        Instances = existing.Instances.SetItems(kvp.Value.Instances)
                    }
                );
        }
    }

    private IEnumerable<KeyValuePair<string, InstanceCollection>> GetChanges(
        IReadOnlyCollection<object> instances
    )
    {
        foreach (var g in instances.GroupBy(x => x.GetType()))
        {
            var collection = CollectionsByType.GetValueOrDefault(g.Key);
            if (collection == null)
                throw new ArgumentException(
                    $"Type {g.Key.FullName} is not mapped to data source.",
                    nameof(instances)
                );
            var typeProvider = TypeSources.GetValueOrDefault(collection);
            if (typeProvider == null)
                throw new ArgumentException(
                    $"Type {g.Key.FullName} is not mapped to data source.",
                    nameof(instances)
                );
            yield return new(
                typeProvider.CollectionName,
                new()
                {
                    Instances = g.ToImmutableDictionary(typeProvider.GetKey),
                    GetKey = typeProvider.GetKey
                }
            );
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
        return this with { Store = newStore, Version = Hub.Version, };
    }

    private EntityStore CreateNewStore(ChangeItem<EntityStore> item) =>
        Store with
        {
            Collections = Store.Collections.SetItems(
                item.Value.Collections.Select(kvp => new KeyValuePair<string, InstanceCollection>(
                    kvp.Key,
                    TypeSources.TryGetValue(kvp.Key, out var ts)
                    && ts is IPartitionedTypeSource
                    && Store.Collections.TryGetValue(kvp.Key, out var existing)
                        ? existing.Merge(kvp.Value)
                        : kvp.Value
                ))
            )
        };

    public WorkspaceState Update(
        string collection,
        Func<InstanceCollection, InstanceCollection> change
    ) => this with { Store = Store.Update(collection, change) };

    public WorkspaceState Update(Func<EntityStore, EntityStore> change) =>
        this with
        {
            Store = change(Store)
        };

    public WorkspaceState Update(object instance)
    {
        if (instance == null)
            throw new ArgumentNullException(nameof(instance));
        var type = instance.GetType();
        if (!CollectionsByType.TryGetValue(type, out var collection))
            throw new InvalidOperationException(
                $"Type {type.FullName} is not mapped to data source."
            );
        var typeProvider = TypeSources.GetValueOrDefault(collection);
        if (typeProvider == null)
            throw new InvalidOperationException(
                $"Type {type.FullName} is not mapped to data source."
            );
        return Update(collection, c => c.Update(typeProvider.GetKey(instance), instance));
    }

    public ITypeSource GetTypeSource(Type type) =>
        TypeSources.GetValueOrDefault(GetCollectionName(type));
}
