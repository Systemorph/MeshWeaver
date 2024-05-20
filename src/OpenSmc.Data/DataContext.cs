using System.Collections.Immutable;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public sealed record DataContext(IMessageHub Hub) : IAsyncDisposable
{
    internal ImmutableDictionary<object, IDataSource> DataSources { get; private set; } =
        ImmutableDictionary<object, IDataSource>.Empty;

    public IDataSource GetDataSource(object id) => DataSources.GetValueOrDefault(id);

    public IEnumerable<Type> MappedTypes => DataSources.Values.SelectMany(ds => ds.MappedTypes);

    public DataContext WithDataSourceBuilder(object id, DataSourceBuilder dataSourceBuilder) =>
        this with
        {
            DataSourceBuilders = DataSourceBuilders.Add(id, dataSourceBuilder),
        };

    public ITypeSource GetTypeSource(Type type) =>
        DataSources
            .Values.Select(ds => ds.TypeSources.GetValueOrDefault(type))
            .FirstOrDefault(ts => ts is not null);

    public ValueTask<EntityStore> Initialized =>
        DataSources
            .Values.ToAsyncEnumerable()
            .SelectAwait(async ds => await ds.Initialized)
            .AggregateAsync(new EntityStore(), (store, el) => store.Merge(el));
    public ImmutableDictionary<object, DataSourceBuilder> DataSourceBuilders { get; set; } =
        ImmutableDictionary<object, DataSourceBuilder>.Empty;
    internal ReduceManager<WorkspaceState> ReduceManager { get; init; } = CreateReduceManager();
    internal TimeSpan InitializationTimeout { get; set; } = TimeSpan.FromHours(60);

    public DataContext WithInitializationTieout(TimeSpan timeout) =>
        this with
        {
            InitializationTimeout = timeout
        };

    public DataContext AddWorkspaceReferenceStream<TReference, TStream>(
        ReducedStreamProjection<WorkspaceState, TReference, TStream> referenceDefinition
    )
        where TReference : WorkspaceReference<TStream> =>
        AddWorkspaceReferenceStream(referenceDefinition, null);

    public DataContext AddWorkspaceReferenceStream<TReference, TStream>(
        ReducedStreamProjection<WorkspaceState, TReference, TStream> referenceDefinition,
        Func<WorkspaceState, TReference, ChangeItem<TStream>, ChangeItem<WorkspaceState>> backFeed
    )
        where TReference : WorkspaceReference<TStream> =>
        this with
        {
            ReduceManager = ReduceManager.AddWorkspaceReferenceStream(
                referenceDefinition,
                backFeed
            ),
        };

    private ImmutableDictionary<
        Type,
        Func<IChangeStream, IChangeItem>
    > StreamConfigration { get; init; } =
        ImmutableDictionary<Type, Func<IChangeStream, IChangeItem>>.Empty;

    public DataContext AddWorkspaceReference<TReference, TStream>(
        Func<WorkspaceState, TReference, TStream> referenceDefinition,
        Func<WorkspaceState, TReference, ChangeItem<TStream>, ChangeItem<WorkspaceState>> backfeed
    )
        where TReference : WorkspaceReference<TStream> =>
        this with
        {
            ReduceManager = ReduceManager.AddWorkspaceReference(referenceDefinition, backfeed)
        };

    public delegate IDataSource DataSourceBuilder(IMessageHub hub);

    public void Initialize()
    {
        DataSources = DataSourceBuilders.ToImmutableDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Invoke(Hub)
        );

        foreach (var dataSource in DataSources.Values)
            dataSource.Initialize();
    }

    internal static ReduceManager<WorkspaceState> CreateReduceManager()
    {
        return new ReduceManager<WorkspaceState>()
            .AddWorkspaceReference<EntityReference, object>(
                (ws, reference) => ws.Store.ReduceImpl(reference),
                null
            )
            .AddWorkspaceReference<PartitionedCollectionsReference, EntityStore>(
                (ws, reference) => ws.ReduceImpl(reference),
                (ws, reference, update) =>
                    update.SetValue(ws with { Store = ws.Store.Update(reference, update.Value) })
            )
            .AddWorkspaceReference<CollectionReference, InstanceCollection>(
                (ws, reference) => ws.Store.ReduceImpl(reference),
                (ws, reference, update) =>
                    update.SetValue(ws with { Store = ws.Store.Update(reference, update.Value) })
            )
            .AddWorkspaceReference<CollectionsReference, EntityStore>(
                (ws, reference) => ws.Store.ReduceImpl(reference),
                (ws, reference, update) =>
                    update.SetValue(ws with { Store = ws.Store.Update(reference, update.Value) })
            )
            .AddWorkspaceReference<WorkspaceStoreReference, EntityStore>(
                (ws, reference) => ws.Store,
                (ws, reference, update) =>
                    update.SetValue(ws with { Store = ws.Store.Update(reference, update.Value) })
            )
            .AddWorkspaceReference<WorkspaceStateReference, WorkspaceState>(
                (ws, reference) => ws,
                (ws, reference, update) =>
                    update.SetValue(ws with { Store = ws.Store.Merge(update.Value.Store) })
            )
            .ForReducedStream<EntityStore>(reduced =>
                reduced
                    .AddWorkspaceReference<EntityReference, object>(
                        (ws, reference) => ws.ReduceImpl(reference),
                        null
                    )
                    // .AddWorkspaceReference<PartitionedCollectionsReference, EntityStore>(
                    //     (ws, reference) => ws.ReduceImpl(reference),
                    //     CreateState
                    // )
                    .AddWorkspaceReference<CollectionReference, InstanceCollection>(
                        (ws, reference) => ws.ReduceImpl(reference),
                        null
                    )
                    .AddWorkspaceReference<CollectionsReference, EntityStore>(
                        (ws, reference) => ws.ReduceImpl(reference),
                        null
                    )
                    .AddWorkspaceReference<WorkspaceStoreReference, EntityStore>(
                        (ws, reference) => ws,
                        null
                    )
            )
            .ForReducedStream<InstanceCollection>(reduced =>
                reduced.AddWorkspaceReference<EntityReference, object>(
                    (ws, reference) => ws.Instances.GetValueOrDefault(reference.Id),
                    null
                )
            // .AddWorkspaceReference<PartitionedCollectionsReference, EntityStore>(
            //     (ws, reference) => ws.ReduceImpl(reference),
            //     CreateState
            // )
            );
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var dataSource in DataSources.Values)
        {
            await dataSource.DisposeAsync();
        }
    }
}
