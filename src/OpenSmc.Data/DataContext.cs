using OpenSmc.Messaging;
using System.Collections.Immutable;

namespace OpenSmc.Data;

public record DataContext(IMessageHub Hub)
{

    internal ImmutableDictionary<Type, object> DataSourcesByTypes { get; init; } = ImmutableDictionary<Type, object>.Empty;
    internal DataContext WithType<T>(object dataSourceId) => 
            this with { DataSourcesByTypes = DataSourcesByTypes.SetItem(typeof(T), dataSourceId) };



    internal ImmutableDictionary<object,DataSource> DataSources { get; init; } = ImmutableDictionary<object, DataSource>.Empty;

    public DataContext WithDataSource(object id, Func<DataSource, DataSource> dataSourceBuilder) 
        => WithDataSource(id, dataSourceBuilder.Invoke(new(id)));

    public DataContext WithDataSource(object id, Func<DataSourceWithStorage, DataSourceWithStorage> dataSourceBuilder, Func<DataSourceWithStorage, IDataStorage> storageFactory)
        => WithDataSource(id, dataSourceBuilder.Invoke(new (id, storageFactory)));

    private DataContext WithDataSource(object id, DataSource dataSource)
    {
        return this 
            with
            {
                DataSources = DataSources.Add(id, dataSource),
                // maintain mapping between data sources and types
                DataSourcesByTypes = DataSourcesByTypes.SetItems(dataSource.MappedTypes.Select(s => new KeyValuePair<Type, object>(s,dataSource.Id)))
            };
    }

    internal DataContext Build()
        => this with
        {
            DataSources = DataSources.Select(kvp => new KeyValuePair<object, DataSource>(kvp.Key, kvp.Value.Build()))
                .ToImmutableDictionary()
        };


    public bool GetTypeConfiguration(Type type, out TypeSource typeSource)
    {
        typeSource = null;
        return DataSourcesByTypes.TryGetValue(type, out var dataSourceId) 
               && DataSources.TryGetValue(dataSourceId, out var dataSource)
               && dataSource.GetTypeConfiguration(type, out typeSource);
    }

    internal ImmutableList<Func<object, object>> InstanceToDataSourceMaps = ImmutableList<Func<object, object>>.Empty;

    private object MapByType(Type type)
    {
        return DataSourcesByTypes.GetValueOrDefault(type);
    }

    internal DataContext MapInstanceToDataSource<T>(Func<T, object> dataSourceMap) => this with
    {
        InstanceToDataSourceMaps = InstanceToDataSourceMaps.Insert(0, o => o is T t ? dataSourceMap.Invoke(t) : default)
    };


    public DataSource GetDataSource(object id)
    {
        return DataSources.GetValueOrDefault(id);
    }
    public object GetDataSourceId(object instance)
    {
        return InstanceToDataSourceMaps.Select(m => m(instance)).FirstOrDefault(id => id != null)
            ?? MapByType(instance.GetType());
    }

}