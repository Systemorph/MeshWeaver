using OpenSmc.Messaging;
using System.Collections.Immutable;
using System.Data;

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

        return await DataSources
            .Values
            .ToAsyncEnumerable()
            .SelectAwait(async ds => await ds.InitializeAsync(cancellationToken))
            .AggregateAsync((ws1, ws2) => ws1.Merge(ws2), cancellationToken: cancellationToken);

    }



    private object MapToDataSource(object instance)
    {
        return DataSources
            .Values
            .Select(ds => ds.MapInstanceToPartition(instance)).FirstOrDefault(x => x != null);
    }

    public async Task UpdateAsync(IReadOnlyCollection<DataChangeRequest> changes, CancellationToken cancellationToken)
    {
        foreach (var databaseGroup in 
                 changes.OfType<DataChangeRequestWithElements>()
                     .SelectMany(c =>
                         c.Elements
                             .GroupBy(MapToDataSource)
                             .Select(g => new { DataSource = g.Key, Request = c with { Elements = g.ToArray() } }
                             ))
                     .GroupBy(x => x.DataSource))
        {
            if (databaseGroup.Key == null)
                throw new DataException("Could not map entities");
            var dataSource = GetDataSource(databaseGroup.Key);
            await dataSource.UpdateAsync(databaseGroup.Select(x => x.Request).ToArray(), cancellationToken);
        }
    }
}