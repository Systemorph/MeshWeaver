#nullable enable
namespace MeshWeaver.Data;

/// <summary>
/// A no-op transaction whose commit and revert do nothing; used where a storage backend has no
/// real transaction semantics.
/// </summary>
public record EmptyTransaction : ITransaction
{
    private EmptyTransaction()
    {
    }

    /// <summary>
    /// The shared singleton instance.
    /// </summary>
    public static readonly EmptyTransaction Instance = new EmptyTransaction();

    /// <summary>
    /// Disposes the transaction; a no-op that completes synchronously.
    /// </summary>
    /// <returns>A completed value task.</returns>
    public ValueTask DisposeAsync()
        => default;

    /// <summary>
    /// Commits the transaction; a no-op that completes immediately.
    /// </summary>
    /// <param name="cancellationToken">Unused; present to satisfy the interface.</param>
    /// <returns>A completed task.</returns>
    public Task CommitAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Reverts the transaction; a no-op that completes immediately.
    /// </summary>
    /// <returns>A completed task.</returns>
    public Task RevertAsync()
        => Task.CompletedTask;
}

/// <summary>
/// A transaction that delegates commit and revert to supplied callbacks.
/// </summary>
/// <param name="CommitAsync">Delegate invoked to commit the transaction.</param>
/// <param name="RevertAsync">Delegate invoked to revert the transaction (also called on dispose).</param>
public record DelegateTransaction(Func<CancellationToken, Task> CommitAsync, Func<Task> RevertAsync) : ITransaction
{
    /// <summary>
    /// Creates a delegate transaction from synchronous commit and revert actions.
    /// </summary>
    /// <param name="commit">Action invoked to commit the transaction.</param>
    /// <param name="revert">Action invoked to revert the transaction.</param>
    public DelegateTransaction(Action commit, Action revert)
        : this(_ =>
        {
            commit();
            return Task.CompletedTask;
        }, () =>
        {
            revert();
            return Task.CompletedTask;
        })
    { }
    /// <summary>
    /// Disposes the transaction by invoking the revert delegate.
    /// </summary>
    /// <returns>A value task that completes when revert finishes.</returns>
    public async ValueTask DisposeAsync()
        => await RevertAsync();

    Task ITransaction.CommitAsync(CancellationToken cancellationToken)
        => CommitAsync(cancellationToken);

    Task ITransaction.RevertAsync()
        => RevertAsync();
}