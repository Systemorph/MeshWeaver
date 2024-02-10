using OpenSmc.DataSource.Abstractions;
using OpenSmc.Messaging;
using System.Collections.Immutable;

namespace OpenSmc.Data;

public record DataConfiguration
{

    internal ImmutableDictionary<Type, DataSource> DataSourcesByTypes { get; init; } = ImmutableDictionary<Type, DataSource>.Empty;
    internal DataConfiguration WithType<T>(DataSource config) => 
            this with { DataSourcesByTypes = DataSourcesByTypes.SetItem(typeof(T), config) };



    internal ImmutableDictionary<object,DataSource> DataSources { get; init; } = ImmutableDictionary<object, DataSource>.Empty;
    public IMessageHub Hub { get; init; }

    public DataConfiguration WithDataSource(object id, Func<DataSource, DataSource> dataSourceBuilder) 
        => WithDataSource(id, dataSourceBuilder.Invoke(new(this)));

    public DataConfiguration WithDataSource(object id, Func<DataSourceWithStorage, DataSourceWithStorage> dataSourceBuilder, Func<DataSource, IDataStorage> storageFactory)
        => WithDataSource(id, dataSourceBuilder.Invoke(new (storageFactory, this)));

    private DataConfiguration WithDataSource(object id, DataSource dataSource)
    {
        return dataSource.Parent // could be modified during config
            with { DataSources = DataSources.Add(id, dataSource.Initialize()) };
    }


    public bool GetTypeConfiguration(Type type, out TypeConfiguration typeConfiguration)
    {
        typeConfiguration = null;
        return DataSourcesByTypes.TryGetValue(type, out var dataSource) &&
               dataSource.GetTypeConfiguration(type, out typeConfiguration);
    }

    internal ImmutableList<Func<object, object>> InstanceToDataSourceMaps = ImmutableList<Func<object, object>>.Empty;

    public DataConfiguration(IMessageHub Hub)
    {
        this.Hub = Hub;
        InstanceToDataSourceMaps = InstanceToDataSourceMaps.Add(o => DataSourcesByTypes.GetValueOrDefault(o.GetType()));
    }

    internal DataConfiguration MapInstanceToDataSource<T>(Func<T, object> dataSourceMap) => this with
    {
        InstanceToDataSourceMaps = InstanceToDataSourceMaps.Insert(0, o => o is T t ? dataSourceMap.Invoke(t) : default)
    };


    public DataSource GetDataSource(object id)
    {
        return id == null ? null : DataSources.GetValueOrDefault(id);
    }
    public object GetDataSourceId(object instance)
    {
        return InstanceToDataSourceMaps.Select(m => m(instance)).FirstOrDefault(id => id != null);
    }

}


public record DataSourceWithStorage(Func<DataSource, IDataStorage> StorageFactory, DataConfiguration Parent) : DataSource(Parent)
{
    public DataSourceWithStorage WithType<T>(Func<TypeConfigurationWithDataStorage<T>, TypeConfigurationWithDataStorage<T>> configurator)
        where T : class
        => (DataSourceWithStorage)WithType(configurator.Invoke(new TypeConfigurationWithDataStorage<T>(StorageFactory)));
    public DataSourceWithStorage WithType<T>()
        where T : class
        => WithType<T>(c => c);


}
public record DataSource(DataConfiguration Parent)
{

    public DataSource WithType<T>(
        Func<TypeConfiguration<T>, TypeConfiguration<T>> configurator)
        where T : class
        => WithType(configurator.Invoke(new TypeConfiguration<T>()));


    public async Task<WorkspaceState> DoInitialize()
    {
        var ret = new WorkspaceState(this);

        foreach (var typeConfiguration in TypeConfigurations.Values)
        {
            var initialized = await typeConfiguration.DoInitialize();
            ret = ret.SetData(typeConfiguration.ElementType, initialized);
        }

        return ret;
    }
    protected DataSource WithType<T>(TypeConfiguration<T> typeConfiguration)
        where T : class
    {
        return this with
        {
            TypeConfigurations = TypeConfigurations.SetItem(typeof(T), typeConfiguration),
            Parent = Parent.WithType<T>(this)
        };
    }


    protected ImmutableDictionary<Type, TypeConfiguration> TypeConfigurations { get; init; } = ImmutableDictionary<Type, TypeConfiguration>.Empty;


    public DataSource WithTransaction(Func<Task<ITransaction>> startTransaction)
        => this with { StartTransactionAsync = startTransaction };


    internal Func<Task<ITransaction>> StartTransactionAsync { get; init; }
        = () => Task.FromResult<ITransaction>(EmptyTransaction.Instance);

    public IEnumerable<Type> MappedTypes => TypeConfigurations.Keys;

    public bool GetTypeConfiguration(Type type, out TypeConfiguration typeConfiguration)
    {
        return TypeConfigurations.TryGetValue(type, out typeConfiguration);
    }

    internal DataSource Initialize() 
        => this with
        {
            TypeConfigurations = TypeConfigurations
                .Select(kvp => new KeyValuePair<Type, TypeConfiguration>(kvp.Key, kvp.Value.Initialize(this)))
                .ToImmutableDictionary()
        };
}

public record EmptyTransaction : ITransaction
{
    private EmptyTransaction()
    {
    }

    public static readonly EmptyTransaction Instance = new EmptyTransaction();

    public ValueTask DisposeAsync()
        => default;

    public Task CommitAsync()
        => Task.CompletedTask;

    public Task RevertAsync()
        => Task.CompletedTask;
}

