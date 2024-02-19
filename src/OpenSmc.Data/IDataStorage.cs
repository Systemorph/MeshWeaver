namespace OpenSmc.Data;

public interface IDataStorage 
{
    Task<IReadOnlyCollection<T>> GetData<T>(CancellationToken cancellationToken) where T : class;
    Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken);
    void Add<T>(IReadOnlyCollection<T> instances) where T : class;
    void Update<T>(IReadOnlyCollection<T> instances) where T:class;
    void Delete<T>(IReadOnlyCollection<T> instances) where T : class;
}

public interface ITransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
    Task RevertAsync();
}