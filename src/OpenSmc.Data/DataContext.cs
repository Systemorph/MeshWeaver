using OpenSmc.Messaging;
using System.Collections.Immutable;

namespace OpenSmc.Data;

public sealed record DataContext(IMessageHub Hub, IWorkspace Workspace)
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

        var workspace = await DataSources
            .Values
            .ToAsyncEnumerable()
            .SelectAwait(async ds => await ds.InitializeAsync(cancellationToken))
            .AggregateAsync((ws1, ws2) => ws1.Merge(ws2), cancellationToken: cancellationToken);

        return workspace;
    }



    public void Update(WorkspaceState ws)
    {
        foreach (var dataSource in DataSources.Values)
            dataSource.Update(ws);
    }
}