using OpenSmc.DataSource.Abstractions;
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
        => WithDataSource(id, dataSourceBuilder.Invoke(new(id, this)));

    public DataContext WithDataSource(object id, Func<DataSourceWithStorage, DataSourceWithStorage> dataSourceBuilder, Func<DataSource, IDataStorage> storageFactory)
        => WithDataSource(id, dataSourceBuilder.Invoke(new (id, storageFactory, this)));

    private DataContext WithDataSource(object id, DataSource dataSource)
    {
        return dataSource.Parent // could be modified during config
            with { DataSources = DataSources.Add(id, dataSource.Build()) };
    }


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


public record DataSourceWithStorage(object Id, Func<DataSource, IDataStorage> StorageFactory, DataContext Parent) : DataSource(Id, Parent)
{
    public IDataStorage Storage { get; private set; }
    public DataSourceWithStorage WithType<T>(Func<TypeSourceWithDataStorage<T>, TypeSourceWithDataStorage<T>> configurator)
        where T : class
        => (DataSourceWithStorage)WithType(configurator.Invoke(new TypeSourceWithDataStorage<T>()));
    public DataSourceWithStorage WithType<T>()
        where T : class
        => WithType<T>(c => c);


    internal override DataSource Build()
    {
        Storage = StorageFactory(this);
        return base.Build();
    }
}
public record DataSource(object Id, DataContext Parent)
{

    public DataSource WithType<T>(
        Func<TypeSource<T>, TypeSource<T>> configurator)
        where T : class
        => WithType(configurator.Invoke(new TypeSource<T>()));


    public async Task<WorkspaceState> DoInitialize()
    {
        var ret = new WorkspaceState(this);

        foreach (var typeConfiguration in TypeSources.Values)
        {
            var initialized = await typeConfiguration.DoInitialize();
            ret = ret.SetData(typeConfiguration.ElementType, initialized);
        }

        return ret;
    }
    protected DataSource WithType<T>(TypeSource<T> typeSource)
        where T : class
    {
        return this with
        {
            TypeSources = TypeSources.SetItem(typeof(T), typeSource),
            Parent = Parent.WithType<T>(Id)
        };
    }


    protected ImmutableDictionary<Type, TypeSource> TypeSources { get; init; } = ImmutableDictionary<Type, TypeSource>.Empty;


    public DataSource WithTransaction(Func<Task<ITransaction>> startTransaction)
        => this with { StartTransactionAsync = startTransaction };


    internal Func<Task<ITransaction>> StartTransactionAsync { get; init; }
        = () => Task.FromResult<ITransaction>(EmptyTransaction.Instance);

    public IEnumerable<Type> MappedTypes => TypeSources.Keys;

    public bool GetTypeConfiguration(Type type, out TypeSource typeSource)
    {
        return TypeSources.TryGetValue(type, out typeSource);
    }

    internal virtual DataSource Build()
    {
        return this with {TypeSources = TypeSources.ToImmutableDictionary(x => x.Key, x => x.Value.Build(this))};
    }
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

