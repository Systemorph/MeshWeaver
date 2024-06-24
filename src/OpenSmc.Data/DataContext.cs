using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Json.Patch;
using Json.Pointer;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public sealed record DataContext : IAsyncDisposable
{
    public DataContext(IMessageHub hub, IWorkspace workspace)
    {
        Hub = hub;
        Workspace = workspace;
        ReduceManager = CreateReduceManager(Hub);
    }

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

    public Task<WorkspaceState> Initialized { get; private set; }

    private async Task<WorkspaceState> CreateInitializationTask()
    {
        var dict = await DataSources
            .Values.ToAsyncEnumerable()
            .SelectAwait(async ds => new KeyValuePair<(object Id, object Reference), EntityStore>(
                (ds.Id, ds.Reference),
                await ds.Initialized
            ))
            .ToArrayAsync();

        return new WorkspaceState(
            Hub,
            DataSources
                .Values.SelectMany(ds =>
                    ds.TypeSources.Select(ts => new { DataSource = ds, TypeSource = ts })
                )
                .ToDictionary(x => x.TypeSource.Key, x => x.DataSource),
            ReduceManager
        )
        {
            StoresByStream = dict.ToImmutableDictionary()
        };
    }

    public ImmutableDictionary<object, DataSourceBuilder> DataSourceBuilders { get; set; } =
        ImmutableDictionary<object, DataSourceBuilder>.Empty;
    internal ReduceManager<WorkspaceState> ReduceManager { get; init; }
    internal TimeSpan InitializationTimeout { get; set; } = TimeSpan.FromHours(60);
    public IMessageHub Hub { get; }
    public IWorkspace Workspace { get; }

    public DataContext WithInitializationTimeout(TimeSpan timeout) =>
        this with
        {
            InitializationTimeout = timeout
        };

    public DataContext ConfigureReduction(
        Func<ReduceManager<WorkspaceState>, ReduceManager<WorkspaceState>> change
    ) => this with { ReduceManager = change.Invoke(ReduceManager) };

    public delegate IDataSource DataSourceBuilder(IMessageHub hub);

    public void Initialize()
    {
        DataSources = DataSourceBuilders.ToImmutableDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Invoke(Hub)
        );

        var state = new WorkspaceState(
            Hub,
            DataSources
                .Values.SelectMany(ds =>
                    ds.TypeSources.Values.Select(ts => new { DataSource = ds, TypeSource = ts })
                )
                .ToDictionary(x => x.TypeSource.ElementType, x => x.DataSource),
            ReduceManager
        );

        foreach (var dataSource in DataSources.Values)
            dataSource.Initialize(state);

        Initialized = CreateInitializationTask();
    }

    internal static ReduceManager<WorkspaceState> CreateReduceManager(IMessageHub hub)
    {
        return new ReduceManager<WorkspaceState>(hub)
            .AddWorkspaceReference<EntityReference, object>(
                (ws, reference) => ws.ReduceImpl(reference)
            )
            .AddWorkspaceReference<PartitionedCollectionsReference, EntityStore>(
                (ws, reference) => ws.ReduceImpl(reference)
            )
            .AddWorkspaceReference<CollectionReference, InstanceCollection>(
                (ws, reference) => ws.ReduceImpl(reference)
            )
            .AddWorkspaceReference<CollectionsReference, EntityStore>(
                (ws, reference) => ws.ReduceImpl(reference)
            )
            .AddWorkspaceReference<WorkspaceStoreReference, EntityStore>(
                (ws, _) => ws.StoresByStream.Values.Aggregate((a, b) => a.Merge(b))
            )
            .AddWorkspaceReference<WorkspaceStateReference, WorkspaceState>((ws, _) => ws)
            .AddBackTransformation<EntityStore>(
                (ws, stream, update) =>
                    update.SetValue(ws.Update((WorkspaceReference)stream.Reference, update.Value))
            )
            .AddBackTransformation<WorkspaceState>(
                (ws, _, update) => update.SetValue(ws.Merge(update.Value))
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
                    .AddWorkspaceReference<CollectionsReference, EntityStore>(
                        (ws, reference) => ws.ReduceImpl(reference)
                    )
                    .AddWorkspaceReference<WorkspaceStoreReference, EntityStore>((ws, _) => ws)
                    .AddBackTransformation<JsonElement>(
                        (current, _, change) =>
                            PatchEntityStore(current, change, hub.JsonSerializerOptions)
                    )
            )
            .ForReducedStream<InstanceCollection>(reduced =>
                reduced.AddWorkspaceReference<EntityReference, object>(
                    (ws, reference) => ws.Instances.GetValueOrDefault(reference.Id)
                )
            )
            .ForReducedStream<JsonElement>(conf =>
                conf.AddWorkspaceReference<JsonPointerReference, JsonElement?>(ReduceJsonPointer)
            );
    }

    private static string GetCollectionName(object reference)
    {
        return reference is CollectionReference coll
            ? coll.Name
            : throw new NotSupportedException();
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

    private static ChangeItem<EntityStore> PatchEntityStore(
        EntityStore current,
        ChangeItem<JsonElement> changeItem,
        JsonSerializerOptions options
    )
    {
        if (changeItem.Patch is not null)
            foreach (var op in changeItem.Patch.Operations)
            {
                switch (op.Path.Segments.Length)
                {
                    case 0:
                        throw new NotSupportedException();
                    case 1:
                        current = UpdateCollection(current, op, op.Path.Segments[0].Value, options);
                        break;
                    default:
                        current = UpdateInstance(
                            current,
                            changeItem.Value,
                            op,
                            op.Path.Segments[0].Value,
                            op.Path.Segments[1].Value,
                            options
                        );
                        break;
                }
            }

        return changeItem.SetValue(changeItem.Value.Deserialize<EntityStore>(options));
    }

    private static EntityStore UpdateInstance(
        EntityStore current,
        JsonElement currentJson,
        PatchOperation op,
        string collection,
        string idSerialized,
        JsonSerializerOptions options
    )
    {
        var id = JsonSerializer.Deserialize<object>(idSerialized.Replace("~1", "/"), options);
        switch (op.Op)
        {
            case OperationType.Add:
            case OperationType.Replace:
                return current.Update(
                    collection,
                    i =>
                        i.Update(
                            id,
                            GetDeserializedValue(collection, idSerialized, currentJson, options)
                        )
                );
            case OperationType.Remove:
                return current.Update(collection, i => i.Remove([id]));
            default:
                throw new NotSupportedException();
        }
    }

    private static object GetDeserializedValue(
        string collection,
        string idSerialized,
        JsonElement currentJson,
        JsonSerializerOptions options
    )
    {
        var pointer = JsonPointer.Parse($"/{collection}/{idSerialized}");
        var el = pointer.Evaluate(currentJson);
        return el?.Deserialize<object>(options);
    }

    private static EntityStore UpdateCollection(
        EntityStore current,
        PatchOperation op,
        string collection,
        JsonSerializerOptions options
    )
    {
        switch (op.Op)
        {
            case OperationType.Add:
            case OperationType.Replace:
                return current.Update(
                    collection,
                    _ => op.Value.Deserialize<InstanceCollection>(options)
                );
            case OperationType.Remove:
                return current.Remove(collection);
            default:
                throw new NotSupportedException();
        }
    }
}
