namespace OpenSmc.Data;

public record DataSourceWithStorage(object Id, Func<DataSourceWithStorage, IDataStorage> StorageFactory)
    : DataSource(Id)
{
    public IDataStorage Storage { get; init; }

    public DataSourceWithStorage WithType<T>(Func<TypeSourceWithDataStorage<T>, TypeSourceWithDataStorage<T>> configurator)
        where T : class
        => (DataSourceWithStorage)WithType(configurator.Invoke(new TypeSourceWithDataStorage<T>()));
    public new DataSourceWithStorage WithType<T>()
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