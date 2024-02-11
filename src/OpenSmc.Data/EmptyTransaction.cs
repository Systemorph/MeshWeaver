namespace OpenSmc.Data;

public record EmptyTransaction : ITransaction
{
    private EmptyTransaction()
    {
    }

    public static readonly EmptyTransaction Instance = new EmptyTransaction();

    public ValueTask DisposeAsync()
        => default;

    public Task CommitAsync()
        => Task.CompletedTask;

    public Task RevertAsync()
        => Task.CompletedTask;
}