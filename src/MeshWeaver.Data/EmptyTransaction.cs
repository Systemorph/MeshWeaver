#nullable enable
namespace MeshWeaver.Data;

public record EmptyTransaction : ITransaction
{
    private EmptyTransaction()
    {
    }

    public static readonly EmptyTransaction Instance = new EmptyTransaction();

    public ValueTask DisposeAsync()
        => default;

    public Task CommitAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task RevertAsync()
        => Task.CompletedTask;
}

public record DelegateTransaction(Func<CancellationToken, Task> CommitAsync, Func<Task> RevertAsync) : ITransaction
{
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
    public async ValueTask DisposeAsync()
        => await RevertAsync();

    Task ITransaction.CommitAsync(CancellationToken cancellationToken)
        => CommitAsync(cancellationToken);

    Task ITransaction.RevertAsync()
        => RevertAsync();
}