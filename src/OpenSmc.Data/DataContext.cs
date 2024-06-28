using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Domain;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public sealed record DataContext : IAsyncDisposable
{
    public DataContext(IWorkspace workspace)
    {
        Hub = workspace.Hub;
        Workspace = workspace;
        ReduceManager = StandardWorkspaceReferenceImplementations.CreateReduceManager(Hub);

        Hub.ServiceProvider.GetRequiredService<ITypeRegistry>().WithKeyFunctionProvider(type =>
                KeyFunctionBuilder.GetFromProperties(
                    type,
                    type.GetProperties().Where(x => x.HasAttribute<DimensionAttribute>()).ToArray()
                )
            );
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
        var state = await DataSources
            .Values.ToAsyncEnumerable()
            .SelectAwait(async ds => await ds.Initialized)
            .AggregateAsync(new WorkspaceState(
                Hub,
                DataSources
                    .Values.SelectMany(ds =>
                        ds.TypeSources.Select(ts => new { DataSource = ds, TypeSource = ts })
                    )
                    .ToDictionary(x => x.TypeSource.Key, x => x.DataSource),
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
    public async ValueTask DisposeAsync()
    {
        foreach (var dataSource in DataSources.Values)
        {
            await dataSource.DisposeAsync();
        }
    }
}
