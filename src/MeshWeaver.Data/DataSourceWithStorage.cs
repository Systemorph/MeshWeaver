using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

/// <summary>
/// Marker interface for data sources that persist their entities through an <see cref="IDataStorage"/> backend.
/// </summary>
public interface IDataSourceWithStorage
{
    /// <summary>
    /// The durable storage backend used to persist this data source's entities.
    /// </summary>
    IDataStorage Storage { get; }
}

/// <summary>
/// Base record for an unpartitioned, type-source-based data source whose changes are persisted to a
/// durable <see cref="IDataStorage"/>. Persistence runs on a dedicated System-identity hub so already
/// authorized changes are written without re-gating on row-level security.
/// </summary>
/// <typeparam name="TDataSource">The concrete data source type (self type).</typeparam>
/// <typeparam name="TTypeSource">The type-source type used to describe registered entity types.</typeparam>
/// <param name="Id">The identity of this data source.</param>
/// <param name="Workspace">The workspace this data source belongs to.</param>
/// <param name="Storage">The durable storage backend used to persist entities.</param>
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

    /// <summary>
    /// Begins a storage transaction in which a batch of changes will be applied.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel starting the transaction.</param>
    /// <returns>A task that completes with the started <see cref="ITransaction"/>.</returns>
    protected virtual Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken)
    {
        return Storage.StartTransactionAsync(cancellationToken);
    }

    /// <summary>
    /// Queues the given change for durable persistence. The write is dispatched onto the
    /// System-identity persistence hub and applied without re-gating on row-level security.
    /// </summary>
    /// <param name="item">The change to persist.</param>
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

    /// <summary>
    /// Applies a change to storage inside a transaction, committing on success and reverting on failure.
    /// </summary>
    /// <param name="workspace">The change to apply to each registered type source.</param>
    /// <param name="cancellationToken">Token used to cancel the update.</param>
    /// <returns>A task that completes when the change has been committed or reverted.</returns>
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

    /// <summary>
    /// Disposes the dedicated persistence hub and the underlying data source.
    /// </summary>
    public override void Dispose()
    {
        persistenceHub.Dispose();
        base.Dispose();
    }
}
