using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public record WorkspaceState(
    IMessageHub Hub,
    IReadOnlyDictionary<Type, IDataSource> DataSources,
    ReduceManager<WorkspaceState> ReduceManager
)
{
    IReadOnlyDictionary<string, ITypeSource> TypeSources { get; } =
        DataSources
            .Values.SelectMany(x => x.TypeSources.Values)
            .ToImmutableDictionary(x => x.CollectionName);

    private ImmutableDictionary<Type, string> CollectionsByType { get; } =
        DataSources
            .Values.SelectMany(x => x.TypeSources.Values)
            .Where(x => x.ElementType != null)
            .ToImmutableDictionary(x => x.ElementType, x => x.CollectionName);

    public string GetCollectionName(Type type) => CollectionsByType.GetValueOrDefault(type);

    public ImmutableDictionary<
        (object Id, object Reference),
        EntityStore
    > StoresByStream { get; init; } =
        ImmutableDictionary<(object Id, object Reference), EntityStore>.Empty;

    public long Version { get; init; } = Hub.Version;

    #region Reducers

    public object Reduce(WorkspaceReference reference) => ReduceManager.Reduce(this, reference);

    public TReference Reduce<TReference>(WorkspaceReference<TReference> reference) =>
        ReduceManager.Reduce(this, reference);

    private InstanceCollection GetPartitionedCollection(
        string collection,
        object partition,
        InstanceCollection instances
    )
    {
        var ret = instances;
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

    // public WorkspaceState Update(
    //     (object Id, object Reference) key,
    //     Func<EntityStore, EntityStore> update
    // )
    // {
    //     return this with
    //         {
    //             StoresByStream = StoresByStream.SetItem(
    //                 key,
    //                 update.Invoke(StoresByStream.GetValueOrDefault(key) ?? new())
    //             ),
    //             Version = Hub.Version
    //         };
    //}

    public WorkspaceState Update(IReadOnlyCollection<object> instances, UpdateOptions options) =>
        Change(new UpdateDataRequest(instances) { Options = options });

    public WorkspaceState Update(WorkspaceReference reference, EntityStore entityStore) =>
        reference switch
        {
            PartitionedCollectionsReference part
                => this with
                {
                    StoresByStream = StoresByStream.SetItems(
                        entityStore
                            .Collections.Select(x => new
                            {
                                TypeSource = TypeSources.GetValueOrDefault(x.Key),
                                Instances = x.Value,
                                DataSource = GetDataSource(x.Key)
                            })
                            .Where(x => x.TypeSource != null)
                            .SelectMany(x =>
                                x.Instances.Instances.Select(i => new
                                    {
                                        Partition = x.TypeSource is IPartitionedTypeSource p
                                            ? p.GetPartition(x)
                                            : x.DataSource.Id,
                                        EntityId = i.Key,
                                        Entity = i.Value,
                                        x.TypeSource,
                                        x.DataSource
                                    })
                                    .Where(x => x.Partition != null)
                                    .GroupBy(x => (x.Partition, x.DataSource.Reference))
                                    .Select(x => new KeyValuePair<
                                        (object Id, object Reference),
                                        EntityStore
                                    >(
                                        x.Key,
                                        new EntityStore(
                                            x.GroupBy(y => y.TypeSource.CollectionName)
                                                .ToDictionary(
                                                    y => y.Key,
                                                    y => new InstanceCollection(
                                                        y.ToDictionary(z => z.Entity, z => z.Entity)
                                                    )
                                                )
                                        )
                                    ))
                            )
                    ),
                    Version = Hub.Version
                },
            _
                => this with
                {
                    StoresByStream = StoresByStream.SetItems(
                        entityStore
                            .Collections.Select(x => new
                            {
                                DataSource = GetDataSource(x.Key),
                                Collection = x.Key,
                                Instances = x.Value
                            })
                            .GroupBy(x => x.DataSource)
                            .Select(x => new KeyValuePair<
                                (object Id, object Reference),
                                EntityStore
                            >(
                                (x.Key.Id, x.Key.Reference),
                                new EntityStore(x.ToDictionary(y => y.Collection, x => x.Instances))
                            ))
                    ),
                    Version = Hub.Version
                },
        };

    public WorkspaceState Change(DataChangedRequest request)
    {
        if (request.Elements == null)
            return null;

        if (request is UpdateDataRequest update)
            return this with
            {
                StoresByStream = StoresByStream.SetItems(MergeUpdate(update)),
                Version = Hub.Version
            };

        return this with
        {
            StoresByStream = StoresByStream.SetItems(MergeDelete((DeleteDataRequest)request)),
            Version = Hub.Version
        };
    }

    private IEnumerable<KeyValuePair<(object Id, object Reference), EntityStore>> MergeUpdate(
        UpdateDataRequest request
    )
    {
        return request
            .Elements.GroupBy(e => e.GetType())
            .SelectMany(e => MapToIdAndAddress(e, e.Key))
            .GroupBy(e => (e.Id, e.Reference))
            .Select(e =>
                (
                    e.Key,
                    Store: new EntityStore(
                        e.Select(y => new KeyValuePair<string, InstanceCollection>(
                                y.Collection,
                                new(y.Elements)
                            ))
                            .ToImmutableDictionary()
                    )
                )
            )
            .Select(e => new KeyValuePair<(object Id, object Reference), EntityStore>(
                e.Key,
                !request.Options.Snapshot && StoresByStream.TryGetValue(e.Key, out var existing)
                    ? existing.Merge(e.Store)
                    : e.Store
            ));
    }

    private IEnumerable<(
        object Id,
        object Reference,
        ImmutableDictionary<object, object> Elements,
        string Collection,
        ITypeSource TypeSource
    )> MapToIdAndAddress(IEnumerable<object> e, Type type)
    {
        if (
            !DataSources.TryGetValue(type, out var dataSource)
            || !dataSource.TypeSources.TryGetValue(type, out var ts)
        )
            throw new InvalidOperationException(
                $"Type {type.FullName} is not mapped to data source."
            );

        if (ts is not IPartitionedTypeSource partitioned)
            yield return (
                dataSource.Id,
                dataSource.Reference,
                e.ToImmutableDictionary(x => ts.GetKey(x)),
                GetCollectionName(type),
                ts
            );
        else
            foreach (var partition in e.GroupBy(x => partitioned.GetPartition(x)))
                yield return (
                    partition.Key,
                    dataSource.Reference,
                    partition.ToImmutableDictionary(x => ts.GetKey(x)),
                    GetCollectionName(type),
                    ts
                );
    }

    private IEnumerable<KeyValuePair<(object Id, object Reference), EntityStore>> MergeDelete(
        DeleteDataRequest request
    )
    {
        return request
            .Elements.GroupBy(e => e.GetType())
            .SelectMany(e => MapToIdAndAddress(e, e.Key))
            .GroupBy(e => (e.Id, e.Reference))
            .Select(e => new KeyValuePair<(object Id, object Reference), EntityStore>(
                e.Key,
                StoresByStream.TryGetValue(e.Key, out var existing)
                    ? e.Aggregate(
                        existing,
                        (s, c) => s.Update(c.Collection, v => v.Remove(c.Elements.Keys))
                    )
                    : null
            ))
            .Where(e => e.Value != null);
    }

    public IEnumerable<Type> MappedTypes => CollectionsByType.Keys;

    public void Rollback()
    {
        throw new NotImplementedException();
    }

    public ITypeSource GetTypeSource(Type type) =>
        TypeSources.GetValueOrDefault(GetCollectionName(type));

    internal object ReduceImpl(EntityReference reference)
    {
        var collection = reference.Collection;
        var store = GetStore(GetDataSource(collection));
        return store.Collections.GetValueOrDefault(collection)?.GetData(reference.Id);
    }

    private EntityStore GetStore(IDataSource dataSource) =>
        GetStore(dataSource.Id, dataSource.Reference);

    private EntityStore GetStore(object id, object reference)
    {
        var store = StoresByStream.GetValueOrDefault((id, reference));
        if (store == null)
            throw new DataSourceConfigurationException($"Store for {id} not initialized");
        return store;
    }

    private IDataSource GetDataSource(string collection)
    {
        var typeSource = TypeSources.GetValueOrDefault(collection);
        if (typeSource == null)
            throw new DataSourceConfigurationException($"Collection {collection} not found");
        var dataSource = DataSources.GetValueOrDefault(typeSource.ElementType);
        if (dataSource == null)
            throw new DataSourceConfigurationException($"Type {typeSource.ElementType} not found");
        return dataSource;
    }

    internal EntityStore ReduceImpl(PartitionedCollectionsReference reference) =>
        reference
            .Reference.Collections.Select(c => TypeSources.GetValueOrDefault(c))
            .Select(ts => new
            {
                Id = ts is IPartitionedTypeSource partitioned
                    ? partitioned.GetPartition(reference.Partition)
                    : DataSources.GetValueOrDefault(ts.ElementType).Id,
                Collection = ts.CollectionName
            })
            .GroupBy(x => x.Id)
            .Select(x =>
                GetStore(x.Key, new CollectionsReference(x.Select(y => y.Collection).ToArray()))
            )
            .Aggregate((store, el) => store.Merge(el));

    internal InstanceCollection ReduceImpl(CollectionReference reference)
    {
        return GetStore(GetDataSource(reference.Name))
            ?.Collections.GetValueOrDefault(reference.Name);
    }

    internal EntityStore ReduceImpl(CollectionsReference reference)
    {
        return new(
            reference
                .Collections.Select(x => new { DataSource = GetDataSource(x), Collection = x })
                .GroupBy(x => x.DataSource)
                .Select(x => new
                {
                    DataSource = x.Key,
                    Store = StoresByStream
                        .GetValueOrDefault((x.Key.Id, x.Key.Reference))
                        ?.ReduceImpl(
                            new CollectionsReference(x.Select(y => y.Collection).ToArray())
                        ),
                })
                .Where(x => x.Store != null)
                .SelectMany(x => x.Store.Collections)
                .ToImmutableDictionary()
        );
    }

    public WorkspaceState Merge(WorkspaceState that) =>
        this with
        {
            StoresByStream = StoresByStream.SetItems(that.StoresByStream)
        };
}
