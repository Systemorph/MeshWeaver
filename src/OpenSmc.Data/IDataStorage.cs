namespace OpenSmc.Data;

public interface IDataStorage 
{
    IQueryable<T> Query<T>() where T : class;
    Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken);
    void Add<T>(IEnumerable<T> instances) where T : class;
    void Update<T>(IEnumerable<T> instances) where T:class;
    void Delete<T>(IEnumerable<T> instances) where T : class;
}

public interface ITransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
    Task RevertAsync();
}