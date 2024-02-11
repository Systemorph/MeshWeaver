using OpenSmc.Data;

namespace OpenSmc.DataSource.Abstractions
{
    public interface IDataStorage : IQuerySource
    {
        Task<ITransaction> StartTransactionAsync();
        void Add<T>(IReadOnlyCollection<T> instances) where T : class;
        void Update<T>(IReadOnlyCollection<T> instances) where T:class;
        void Delete<T>(IReadOnlyCollection<T> instances) where T : class;
    }

    public interface ITransaction : IAsyncDisposable
    {
        Task CommitAsync();
        Task RevertAsync();
    }
}