namespace OpenSmc.Data;

public record DataSourceWithStorage : DataSource
{
    public DataSourceWithStorage(object Id, Func<DataSourceWithStorage, IDataStorage> StorageFactory) : base(Id)
    {
        this.StorageFactory = StorageFactory;
    }

    public IDataStorage Storage { get; init; }
    public Func<DataSourceWithStorage, IDataStorage> StorageFactory { get; init; }

    public DataSourceWithStorage WithType<T>(Func<TypeSourceWithDataStorage<T>, TypeSourceWithDataStorage<T>> configurator)
        where T : class
        => (DataSourceWithStorage)WithType(configurator.Invoke(new TypeSourceWithDataStorage<T>()));
    public DataSourceWithStorage WithType<T>()
        where T : class
        => WithType<T>(c => c);


    protected override DataSource Buildup()
    {
        var storage = StorageFactory(this);
        return this with
        {
            Storage = storage,
            StartTransactionAction = storage.StartTransactionAsync
        };
    }
}