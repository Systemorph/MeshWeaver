using OpenSmc.Messaging;
using System.Collections.Immutable;
using System.Data;
using OpenSmc.Data.Persistence;

namespace OpenSmc.Data;

public sealed record DataContext(IMessageHub Hub)
{
    internal ImmutableDictionary<object,IDataSource> DataSources { get; private set; } = ImmutableDictionary<object, IDataSource>.Empty;

    public IDataSource GetDataSource(object id) => DataSources.GetValueOrDefault(id);

    public IEnumerable<Type> MappedTypes => DataSources.Values.SelectMany(ds => ds.MappedTypes);

    public DataContext WithDataSourceBuilder(object id, DataSourceBuilder dataSourceBuilder)
    {
        return this with
        {
            DataSourceBuilders = DataSourceBuilders.Add(id, dataSourceBuilder),
        };
    }

    public ImmutableDictionary<object, DataSourceBuilder> DataSourceBuilders { get; set; } = ImmutableDictionary<object, DataSourceBuilder>.Empty;

    public delegate IDataSource DataSourceBuilder(IMessageHub hub); 


    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        DataSources = DataSourceBuilders
            .ToImmutableDictionary(kvp => kvp.Key,
                kvp => kvp.Value.Invoke(Hub));
        foreach (var dataSource in DataSources.Values)
        {
            await dataSource.InitializeAsync(cancellationToken);
        }
    }


    public IReadOnlyDictionary<string, IReadOnlyCollection<EntityDescriptor>> GetEntities()
    {
        return DataSources
            .Values
            .SelectMany(ds => ds.GetData().Values.SelectMany(x => x))
            .GroupBy(x => x.Collection)
            .ToDictionary(x => x.Key, x => (IReadOnlyCollection<EntityDescriptor>)x.ToArray());
    }


    public void Synchronize(DataChangedEvent @event, object dataSourceId)
    {
        // update foreign data source
        if (GetDataSource(dataSourceId) is HubDataSource dataSource)
            dataSource.Synchronize(@event);
    
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