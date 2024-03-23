using OpenSmc.Messaging;
using System.Collections.Immutable;

namespace OpenSmc.Data;

public sealed record DataContext(IMessageHub Hub, IWorkspace Workspace, ReduceManager ReduceManager) : IAsyncDisposable
{
    internal ImmutableDictionary<object,IDataSource> DataSources { get; private set; } = ImmutableDictionary<object, IDataSource>.Empty;


    public IDataSource GetDataSource(object id) => DataSources.GetValueOrDefault(id);

    public IEnumerable<Type> MappedTypes => DataSources.Values.SelectMany(ds => ds.MappedTypes);

    public DataContext WithDataSourceBuilder(object id, DataSourceBuilder dataSourceBuilder) => this with
    {
        DataSourceBuilders = DataSourceBuilders.Add(id, dataSourceBuilder),
    };

    public ImmutableDictionary<object, DataSourceBuilder> DataSourceBuilders { get; set; } = ImmutableDictionary<object, DataSourceBuilder>.Empty;

    public delegate IDataSource DataSourceBuilder(IMessageHub hub); 

    public async Task<WorkspaceState> InitializeAsync(CancellationToken cancellationToken)
    {
        DataSources = DataSourceBuilders
            .ToImmutableDictionary(kvp => kvp.Key,
                kvp => kvp.Value.Invoke(Hub));

        var store = await DataSources
            .Values
            .ToAsyncEnumerable()
            .SelectAwait(async ds => await ds.InitializeAsync(cancellationToken))
            .AggregateAsync((ws1, ws2) => new(ws1.Instances.SetItems(ws2.Instances)), cancellationToken: cancellationToken);

        return new(Hub, store,
            DataSources
                .SelectMany(x => x.Value.TypeSources)
                .GroupBy(x => x.CollectionName)
                .ToDictionary(x => x.Key, x => x.First()), 
            ReduceManager
            );
    }



    public void Update(WorkspaceState ws)
    {
        foreach (var dataSource in DataSources.Values)
            dataSource.Update(ws);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var dataSource in DataSources.Values)
        {
            await dataSource.DisposeAsync();
        }
    }
}