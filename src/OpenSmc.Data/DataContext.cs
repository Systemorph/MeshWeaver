using OpenSmc.Messaging;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data.Persistence;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public sealed record DataContext(IMessageHub Hub)
{
    private readonly ISerializationService serializationService =
        Hub.ServiceProvider.GetRequiredService<ISerializationService>();

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

    public ImmutableDictionary<object, DataSourceBuilder> DataSourceBuilders { get; set; }

    public delegate IDataSource DataSourceBuilder(IMessageHub hub); 

    internal ImmutableList<Func<object, object>> InstanceToDataSourceMaps = ImmutableList<Func<object, object>>.Empty;

    internal DataContext MapInstanceToDataSource<T>(Func<T, object> dataSourceMap) => this with
    {
        InstanceToDataSourceMaps = InstanceToDataSourceMaps.Insert(0, o => o is T t ? dataSourceMap.Invoke(t) : default)
    };    

    public object MapInstanceToDataSource(object instance) => DataSources.Values.FirstOrDefault(ds => ds.ContainsInstance(instance));

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

    public ImmutableDictionary<string,ImmutableDictionary<object,object>> GetAllData()
    {
        return DataSources.Values.SelectMany(ds => ds.GetWorkspace().GetData())
            .GroupBy(x => x.Key)
            .ToImmutableDictionary(x => x.Key, x => x.SelectMany(y => y.Value)
                .ToImmutableDictionary(y => y.Key, y => y.Value));
    }

    public JsonNode GetSerializedWorkspace()
    {
        var allData = 
            DataSources
            .Values
            .SelectMany(ds => ds.GetData())
            .GroupBy(kvp => kvp.Key)
            .ToDictionary
            (
                x => x.Key,
                x => (IReadOnlyDictionary<object,object>)x
                    .SelectMany(y => y.Value)
                    .ToDictionary(y => y.Key, y => y.Value)
            );
        return serializationService.SerializeWorkspaceData(allData);
    }

    public void Change(DataChangeRequest request)
    {
        foreach (var g in request.Elements.GroupBy(MapInstanceToDataSource))
        {
            var dataSource = GetDataSource(g.Key);
            dataSource?.Change(request with {Elements = g.ToArray()});
        }
    }
}