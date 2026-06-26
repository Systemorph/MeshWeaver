namespace MeshWeaver.Data;

/// <summary>
/// Abstraction over a persistence backend (e.g. EF Core or in-memory): queryable reads,
/// transactions, and buffered add/update/delete operations.
/// </summary>
public interface IDataStorage 
{
    /// <summary>
    /// Returns a queryable over the stored entities of type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The entity type to query.</typeparam>
    /// <returns>A queryable for composing read queries.</returns>
    IQueryable<T> Query<T>() where T : class;
    /// <summary>
    /// Begins a new transaction over the backend.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task yielding the started transaction.</returns>
    Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken);
    /// <summary>
    /// Stages the given instances for insertion.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="instances">The instances to add.</param>
    void Add<T>(IEnumerable<T> instances) where T : class;
    /// <summary>
    /// Stages the given instances for update.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="instances">The instances to update.</param>
    void Update<T>(IEnumerable<T> instances) where T:class;
    /// <summary>
    /// Stages the given instances for deletion.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="instances">The instances to delete.</param>
    void Delete<T>(IEnumerable<T> instances) where T : class;
}

/// <summary>
/// A unit of work over a data storage backend that can be committed or reverted; disposing
/// reverts any uncommitted work.
/// </summary>
public interface ITransaction : IAsyncDisposable
{
    /// <summary>
    /// Commits all staged changes.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the commit finishes.</returns>
    Task CommitAsync(CancellationToken cancellationToken);
    /// <summary>
    /// Discards all staged changes.
    /// </summary>
    /// <returns>A task that completes when the revert finishes.</returns>
    Task RevertAsync();
}