using System.Collections.Immutable;
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
        StreamReference,
        EntityStore
    > StoresByStream { get; init; } =
        ImmutableDictionary<StreamReference, EntityStore>.Empty;

    public long Version { get; init; } = Hub.Version;

    #region Reducers

    public object Reduce(WorkspaceReference reference) => ReduceManager.Reduce(this, reference);

    public TReference Reduce<TReference>(WorkspaceReference<TReference> reference) =>
        ReduceManager.Reduce(this, reference);


    #endregion

    public WorkspaceState Update(IReadOnlyCollection<object> instances, UpdateOptions options) =>
        Change(new UpdateDataRequest(instances) { Options = options });

    public WorkspaceState Update(EntityStore entityStore) =>
        this with
        {
            StoresByStream = StoresByStream.SetItems(
                entityStore
                    .Collections.Select(x => new
                    {
                        DataSource = GetDataSource(x.Key), Collection = x.Key, Instances = x.Value
                    })
                    .GroupBy(x => x.DataSource)
                    .Select(x => new KeyValuePair<
                        StreamReference,
                        EntityStore
                    >(
                        new(x.Key.Id, x.Key.Reference),
                        new EntityStore(x.ToDictionary(y => y.Collection, y => y.Instances))
                    ))
            ),
            Version = Hub.Version
        };

    public WorkspaceState Update(StreamReference reference, EntityStore store) =>
        this with { StoresByStream = StoresByStream.SetItem(reference, store) };

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

    private IEnumerable<KeyValuePair<StreamReference, EntityStore>> MergeUpdate(
        UpdateDataRequest request
    )
    {
        return request
            .Elements.GroupBy(e => e.GetType())
            .SelectMany(e => MapToIdAndAddress(e, e.Key))
            .GroupBy(e => new StreamReference(e.Id, e.Reference))
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
            .Select(e => new KeyValuePair<StreamReference, EntityStore>(
                e.Key,
                StoresByStream.TryGetValue(e.Key, out var existing)
                    ? existing.Merge(e.Store, request.Options ?? UpdateOptions.Default)
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
                    new PartitionedCollectionsReference(partition.Key,dataSource.Reference),
                    partition.ToImmutableDictionary(x => ts.GetKey(x)),
                    GetCollectionName(type),
                    ts
                );
    }

    private IEnumerable<KeyValuePair<StreamReference, EntityStore>> MergeDelete(
        DeleteDataRequest request
    )
    {
        return request
            .Elements.GroupBy(e => e.GetType())
            .SelectMany(e => MapToIdAndAddress(e, e.Key))
            .GroupBy(e => new StreamReference(e.Id, e.Reference))
            .Select(e => new KeyValuePair<StreamReference, EntityStore>(
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
        dataSource.Streams.Select(s => 
            StoresByStream.GetValueOrDefault(s.StreamReference))
            .Where(x => x != null)
            .Aggregate((x,y) => x.Merge(y)
            );


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


    internal InstanceCollection ReduceImpl(CollectionReference reference)
    {
        return GetStore(GetDataSource(reference.Name))
            ?.Collections.GetValueOrDefault(reference.Name);
    }

    internal EntityStore ReduceImpl(PartitionedCollectionsReference reference) =>
    StoresByStream.GetValueOrDefault(new (reference.Partition, reference))
        ?? ReduceImpl(reference.Reference);

    internal EntityStore ReduceImpl(CollectionsReference reference) =>
        reference
            .Collections.Select(x => new { DataSource = GetDataSource(x), Collection = x })
            .GroupBy(x => x.DataSource)
            .SelectMany(x => x.Key.Streams.Select(p => StoresByStream.GetValueOrDefault(p.StreamReference)))
            .Where(x => x != null)
            .Aggregate((x, y) => x.Merge(y, UpdateOptions.Default));
    internal EntityStore ReduceImpl(StreamReference reference) =>
StoresByStream.GetValueOrDefault(reference);

    public WorkspaceState Merge(WorkspaceState that) =>
        this with
        {
            StoresByStream = StoresByStream.SetItems(that.StoresByStream)
        };
}
