using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

public interface IDataSourceWithStorage
{
    IDataStorage Storage { get; }
}

public abstract record UnpartitionedDataSourceWithStorage<TDataSource, TTypeSource>(
    object Id,
    IWorkspace Workspace,
    IDataStorage Storage
) : TypeSourceBasedUnpartitionedDataSource<TDataSource, TTypeSource>(Id, Workspace), IDataSourceWithStorage
    where TDataSource : UnpartitionedDataSourceWithStorage<TDataSource, TTypeSource> 
    where TTypeSource : ITypeSource
{


    private readonly IMessageHub persistenceHub = Workspace.Hub.ServiceProvider.CreateMessageHub(
        AddressExtensions.CreatePersistenceAddress(),
        // Persistence is framework infrastructure (the store) — it posts as System
        // (feedback_access_context_always_set). Declared at hub startup so any post it
        // makes carries system-security automatically, in addition to the explicit
        // ImpersonateAsSystem around the DB write in Synchronize below.
        c => c.WithPostingIdentity(PostingIdentity.System)
    );

    private readonly AccessService? accessService =
        Workspace.Hub.ServiceProvider.GetService<AccessService>();

    private readonly ILogger logger = Workspace.Hub.ServiceProvider.GetRequiredService<
        ILogger<TDataSource>
    >();

    protected virtual Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken)
    {
        return Storage.StartTransactionAsync(cancellationToken);
    }

    protected override void Synchronize(ChangeItem<EntityStore> item)
    {
        persistenceHub.InvokeAsync(async ct =>
        {
            // 🚨 Persistence runs under SYSTEM. By the time a change reaches this
            // persistence queue it is ALREADY AUTHORIZED — RLS was enforced at the
            // user-facing write (app handler / stream.Update on the hub action block,
            // where the user identity was live). The durable DB write just stores the
            // approved change; it must not re-gate on RLS, and it must NEVER fail-closed
            // on a null ambient AccessContext. Synchronize fires on the workspace
            // emission scheduler, which has wiped the AsyncLocal identity — without this
            // a persist could be denied and silently dropped (e.g. an _Activity node
            // never lands → progress readers resubscribe-storm). See
            // AccessContextPropagation.md → "Persistence runs as System".
            using (accessService?.ImpersonateAsSystem())
                await UpdateAsync(item, ct);
        }, ex =>
        {
            logger.LogWarning(ex,"Updating {DataSource} failed", Id);
            return Task.CompletedTask;
        });
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

    public override void Dispose()
    {
        persistenceHub.Dispose();
        base.Dispose();
    }
}
