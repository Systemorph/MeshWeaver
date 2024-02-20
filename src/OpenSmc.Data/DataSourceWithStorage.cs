using OpenSmc.Messaging;

namespace OpenSmc.Data;

public interface IDataSourceWithStorage
{
    IDataStorage Storage { get; }
}

public abstract record DataSourceWithStorage<TDataSource>(object Id)
    : DataSource<TDataSource>(Id), IDataSourceWithStorage
    where TDataSource : DataSourceWithStorage<TDataSource>
{

    public abstract IDataStorage CreateStorage(IMessageHub hub);

    protected override TDataSource Buildup(IMessageHub hub)
    {
        if(Storage != null)
            return base.Buildup(hub);
        var storage = CreateStorage(hub);
        return (this with
        {
            Storage = storage,
            StartTransactionAction = storage.StartTransactionAsync
        }).Buildup(hub);
    }

    protected override TypeSource<T> CreateTypeSource<T>()
    {
        return new TypeSourceWithDataStorage<T>();
    }


    public IDataStorage Storage { get; init; }
}