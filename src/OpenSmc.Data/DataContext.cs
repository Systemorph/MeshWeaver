using OpenSmc.Messaging;
using System.Collections.Immutable;
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


    public IEnumerable<EntityDescriptor> GetEntities()
    {
        var allData = 
            DataSources
            .Values
            .SelectMany(ds => ds.GetData());
        return allData;
    }


    public void Synchronize(DataChangedEvent @event, object dataSourceId)
    {
        // update foreign data source
        if (GetDataSource(dataSourceId) is HubDataSource dataSource)
            dataSource.Synchronize(@event);
    
    }

    public async Task UpdateAsync(IEnumerable<DataSourceUpdate> changes, CancellationToken cancellationToken)
    {
        foreach (var update in changes.GroupBy(c => c.DataSource))
        {
            var dataSource = GetDataSource(update.Key);
            await dataSource.UpdateAsync(update, cancellationToken);
        }
    }
}