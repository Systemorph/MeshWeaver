namespace OpenSmc.Data;

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