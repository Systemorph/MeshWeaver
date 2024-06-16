using System.Collections.Immutable;
using System.Text.Json;
using Json.Pointer;
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

    public DataContext WithInitializationTimeout(TimeSpan timeout) =>
        this with
        {
            InitializationTimeout = timeout
        };


    public DataContext ConfigureReduction(Func<ReduceManager<WorkspaceState>, ReduceManager<WorkspaceState>> change) =>
        this with
        {
            ReduceManager = change.Invoke(ReduceManager)
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
                (ws, reference) => ws.Store.ReduceImpl(reference)
            )
            .AddWorkspaceReference<PartitionedCollectionsReference, EntityStore>(
                (ws, reference) => ws.ReduceImpl(reference)
            )
            .AddWorkspaceReference<CollectionReference, InstanceCollection>(
                (ws, reference) => ws.Store.ReduceImpl(reference)
            )
            .AddWorkspaceReference<CollectionsReference, EntityStore>(
                (ws, reference) => ws.Store.ReduceImpl(reference)
            )
            .AddWorkspaceReference<WorkspaceStoreReference, EntityStore>(
                (ws, _) => ws.Store
            )
            .AddWorkspaceReference<WorkspaceStateReference, WorkspaceState>(
                (ws, _) => ws
            )
            .AddBackTransformation<PartitionedCollectionsReference, EntityStore>((ws, reference, update) =>
                update.SetValue(ws with { Store = ws.Store.Update(reference, update.Value) })
            )
            .AddBackTransformation<WorkspaceStoreReference, EntityStore>((ws, reference, update) =>
                update.SetValue(ws with { Store = ws.Store.Update(reference, update.Value) })
            )
            .AddBackTransformation<WorkspaceStoreReference, WorkspaceState>((ws, _, update) =>
                update.SetValue(ws with { Store = ws.Store.Merge(update.Value.Store) })
            )
            .AddBackTransformation<CollectionsReference, EntityStore>((ws, reference, update) =>
                update.SetValue(ws with { Store = ws.Store.Update(reference, update.Value) })
            )
            .AddBackTransformation<CollectionReference, InstanceCollection>((ws, reference, update) =>
                update.SetValue(ws with { Store = ws.Store.Update(reference, update.Value) })
            )
            .ForReducedStream<EntityStore>(reduced =>
                reduced
                    .AddWorkspaceReference<EntityReference, object>(
                        (ws, reference) => ws.ReduceImpl(reference)
                    )
                    .AddWorkspaceReference<CollectionReference, InstanceCollection>(
                        (ws, reference) => ws.ReduceImpl(reference)
                    )
                    .AddWorkspaceReference<CollectionsReference, EntityStore>(
                        (ws, reference) => ws.ReduceImpl(reference)
                    )
                    .AddWorkspaceReference<WorkspaceStoreReference, EntityStore>(
                        (ws, _) => ws)
            )
            .ForReducedStream<InstanceCollection>(reduced =>
                reduced.AddWorkspaceReference<EntityReference, object>(
                    (ws, reference) => ws.Instances.GetValueOrDefault(reference.Id)
                )

            )
            .ForReducedStream<JsonElement>(conf => conf.AddWorkspaceReference<JsonPointerReference, JsonElement?>(ReduceJsonPointer));
    }
    private static JsonElement? ReduceJsonPointer(JsonElement obj, JsonPointerReference pointer)
    {
        var parsed = JsonPointer.Parse(pointer.Pointer);
        var result = parsed.Evaluate(obj);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var dataSource in DataSources.Values)
        {
            await dataSource.DisposeAsync();
        }
    }
}
