using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Domain;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public sealed record DataContext : IAsyncDisposable
{
    public ITypeRegistry TypeRegistry { get; }
    public DataContext(IWorkspace workspace)
    {
        Hub = workspace.Hub;
        Workspace = workspace;
        ReduceManager = StandardWorkspaceReferenceImplementations.CreateReduceManager(Hub);

        TypeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        TypeRegistry.WithKeyFunctionProvider(type =>
            KeyFunctionBuilder.GetFromProperties(
                type,
                type.GetProperties().Where(x => x.HasAttribute<DimensionAttribute>()).ToArray()
            )
        );
    }

    private Dictionary<Type, ITypeSource> TypeSourcesByType { get; set; }

    private ImmutableDictionary<object, IDataSource> DataSourcesById { get; set; } =
        ImmutableDictionary<object, IDataSource>.Empty;

    public IDataSource GetDataSourceById(object id) => DataSourcesById.GetValueOrDefault(id);
    public IDataSource GetDataSourceByType(Type type) => DataSourcesByType.GetValueOrDefault(type);

    public IReadOnlyDictionary<Type, IDataSource> DataSourcesByType { get; private set; }

    public DataContext WithDataSourceBuilder(object id, DataSourceBuilder dataSourceBuilder) =>
        this with
        {
            DataSourceBuilders = DataSourceBuilders.Add(id, dataSourceBuilder),
        };

    public IReadOnlyDictionary<string, ITypeSource> TypeSources { get; private set; }

    public ITypeSource GetTypeSource(string collection) =>
        TypeSources.GetValueOrDefault(collection);
    public ITypeSource GetTypeSource(Type type) =>
        TypeSourcesByType.GetValueOrDefault(type);
    public Task<WorkspaceState> Initialized { get; private set; }

    private async Task<WorkspaceState> CreateInitializationTask()
    {
        TypeSources = 
            DataSourcesById
            .Values
            .SelectMany(ds => ds.TypeSources.Values)
            .ToDictionary(x => x.CollectionName);
        TypeSourcesByType = DataSourcesById.Values.SelectMany(ds => ds.TypeSources).ToDictionary();
        DataSourcesByType = DataSourcesById.Values
            .SelectMany(ds => ds.MappedTypes.Select(type => new KeyValuePair<Type, IDataSource>(type, ds)))
            .ToDictionary();
        var state = await DataSourcesById
            .Values.ToAsyncEnumerable()
            .SelectAwait(async ds => await ds.Initialized)
            .AggregateAsync(new WorkspaceState(
                Hub,
                this,
                ReduceManager
            ), (x, y) => x.Merge(y));
        return state;
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

    public DataContext Configure(
        Func<ReduceManager<WorkspaceState>, ReduceManager<WorkspaceState>> change
    ) => this with { ReduceManager = change.Invoke(ReduceManager) };

    public delegate IDataSource DataSourceBuilder(IMessageHub hub);

    public void Initialize()
    {
        DataSourcesById = DataSourceBuilders.ToImmutableDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Invoke(Hub)
        );

        var state = new WorkspaceState(
            Hub,
            this,
            ReduceManager
        );

        foreach (var dataSource in DataSourcesById.Values)
            dataSource.Initialize(state);

        Initialized = CreateInitializationTask();
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var dataSource in DataSourcesById.Values)
        {
            await dataSource.DisposeAsync();
        }
    }

    public string GetCollectionName(Type type)
        => TypeSourcesByType.GetValueOrDefault(type)?.CollectionName;
}
