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
        if(Storage != null)
            return base.Buildup();
        var storage = CreateStorage();
        return (this with
        {
            Storage = storage,
            StartTransactionAction = storage.StartTransactionAsync
        }).Buildup();
    }

    public IDataStorage Storage { get; init; }
}