using OpenSmc.Messaging;

namespace OpenSmc.Data;

public interface IDataSourceWithStorage
{
    IDataStorage Storage { get; }
}

public abstract record DataSourceWithStorage<TDataSource>(object Id, IMessageHub Hub, IDataStorage Storage)
    : DataSource<TDataSource>(Id, Hub), IDataSourceWithStorage
    where TDataSource : DataSourceWithStorage<TDataSource>
{
    protected override Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken)
    {
        return Storage.StartTransactionAsync(cancellationToken);
    }

}