using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Data.Serialization;
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

    public override IEnumerable<ChangeStream<EntityStore>> Initialize()
    {
        Workspace.ChangeStream
            .Subscribe(Update);

        return base.Initialize();
    }

    private void Update(ChangeItem<WorkspaceState> workspace)
    {
        // TODO V10: Should see that there are actual changes ==> no reducer applied here. (25.03.2024, Roland Bürgi)
        persistenceHub.Schedule(ct => UpdateAsync(workspace, ct));
    }

    protected virtual async Task UpdateAsync(ChangeItem<WorkspaceState> workspace, CancellationToken cancellationToken)
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