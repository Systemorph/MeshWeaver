using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public interface IDataSourceWithStorage
{
    IDataStorage Storage { get; }
}

public abstract record DataSourceWithStorage<TDataSource, TTypeSource>(
    object Id,
    IWorkspace Workspace,
    IDataStorage Storage
) : TypeSourceBasedDataSource<TDataSource, TTypeSource>(Id, Workspace), IDataSourceWithStorage
    where TDataSource : DataSourceWithStorage<TDataSource, TTypeSource> 
    where TTypeSource : ITypeSource
{
    private readonly IMessageHub persistenceHub = Workspace.Hub.ServiceProvider.CreateMessageHub(
        new PersistenceAddress(Workspace.Hub.Address),
        c => c
    );

    private readonly ILogger logger = Workspace.Hub.ServiceProvider.GetRequiredService<
        ILogger<TDataSource>
    >();

    protected virtual Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken)
    {
        return Storage.StartTransactionAsync(cancellationToken);
    }

    protected override void Synchronize(ChangeItem<EntityStore> item)
    {
        persistenceHub.InvokeAsync(ct => UpdateAsync(item, ct));
    }

    protected virtual async Task UpdateAsync(
        ChangeItem<EntityStore> workspace,
        CancellationToken cancellationToken
    )
    {
        await using ITransaction transaction = await StartTransactionAsync(cancellationToken);
        try
        {
            foreach (var ts in TypeSources.Values)
                ts.Update(workspace);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Transaction failed: {exception}", ex);
            await transaction.RevertAsync();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await persistenceHub.DisposeAsync();
        await base.DisposeAsync();
    }
}
