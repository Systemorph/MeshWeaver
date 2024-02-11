using OpenSmc.DataSource.Abstractions;
using OpenSmc.Messaging;
using System.Collections.Immutable;

namespace OpenSmc.Data;

public record DataConfiguration(IMessageHub Hub)
{

    internal ImmutableDictionary<Type, object> DataSourcesByTypes { get; init; } = ImmutableDictionary<Type, object>.Empty;
    internal DataConfiguration WithType<T>(object dataSourceId) => 
            this with { DataSourcesByTypes = DataSourcesByTypes.SetItem(typeof(T), dataSourceId) };



    internal ImmutableDictionary<object,DataSource> DataSources { get; init; } = ImmutableDictionary<object, DataSource>.Empty;

    public DataConfiguration WithDataSource(object id, Func<DataSource, DataSource> dataSourceBuilder) 
        => WithDataSource(id, dataSourceBuilder.Invoke(new(id, this)));

    public DataConfiguration WithDataSource(object id, Func<DataSourceWithStorage, DataSourceWithStorage> dataSourceBuilder, Func<DataSource, IDataStorage> storageFactory)
        => WithDataSource(id, dataSourceBuilder.Invoke(new (id, storageFactory, this)));

    private DataConfiguration WithDataSource(object id, DataSource dataSource)
    {
        return dataSource.Parent // could be modified during config
            with { DataSources = DataSources.Add(id, dataSource.Initialize()) };
    }


    public bool GetTypeConfiguration(Type type, out TypeConfiguration typeConfiguration)
    {
        typeConfiguration = null;
        return DataSourcesByTypes.TryGetValue(type, out var dataSourceId) 
               && DataSources.TryGetValue(dataSourceId, out var dataSource)
               && dataSource.GetTypeConfiguration(type, out typeConfiguration);
    }

    internal ImmutableList<Func<object, object>> InstanceToDataSourceMaps = ImmutableList<Func<object, object>>.Empty;

    private object MapByType(Type type)
    {
        return DataSourcesByTypes.GetValueOrDefault(type);
    }

    internal DataConfiguration MapInstanceToDataSource<T>(Func<T, object> dataSourceMap) => this with
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


public record DataSourceWithStorage(object Id, Func<DataSource, IDataStorage> StorageFactory, DataConfiguration Parent) : DataSource(Id, Parent)
{
    public DataSourceWithStorage WithType<T>(Func<TypeConfigurationWithDataStorage<T>, TypeConfigurationWithDataStorage<T>> configurator)
        where T : class
        => (DataSourceWithStorage)WithType(configurator.Invoke(new TypeConfigurationWithDataStorage<T>(StorageFactory)));
    public DataSourceWithStorage WithType<T>()
        where T : class
        => WithType<T>(c => c);


}
public record DataSource(object Id, DataConfiguration Parent)
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
            Parent = Parent.WithType<T>(Id)
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

