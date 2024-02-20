namespace OpenSmc.Data;

public interface IDataSourceWithStorage
{
    IDataStorage Storage { get; }
}

public abstract record DataSourceWithStorage<TDataSource>(object Id)
    : DataSource<TDataSource>(Id), IDataSourceWithStorage
    where TDataSource : DataSourceWithStorage<TDataSource>
{

    public abstract IDataStorage CreateStorage();

    protected override TDataSource Buildup()
    {
        var storage = CreateStorage();
        return (TDataSource)this with
        {
            Storage = storage,
            StartTransactionAction = storage.StartTransactionAsync
        };
    }

    public IDataStorage Storage { get; init; }
}