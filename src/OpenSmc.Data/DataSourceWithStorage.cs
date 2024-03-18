using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private readonly IMessageHub persistenceHub =
        Hub.ServiceProvider.CreateMessageHub(new PersistenceAddress(Hub.Address), c => c);

    private readonly ILogger logger = Hub.ServiceProvider.GetRequiredService<ILogger<TDataSource>>();
    protected virtual Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken)
    {
        return Storage.StartTransactionAsync(cancellationToken);
    }

    protected virtual async Task UpdateAsync(WorkspaceState workspace, CancellationToken cancellationToken)
    {
        await using ITransaction transaction = await StartTransactionAsync(cancellationToken);
        try
        {
            base.Update(workspace);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Transaction failed: {exception}", ex);
            await transaction.RevertAsync();
        }
    }

    public override EntityStore Update(WorkspaceState workspace)
    {
        persistenceHub.Schedule(c => UpdateAsync(workspace, c));
        return workspace.Store;
    }

    public override async ValueTask DisposeAsync()
    {
        await persistenceHub.DisposeAsync();
        await base.DisposeAsync();
    }
}