using Microsoft.EntityFrameworkCore;
using OpenSmc.Data;

namespace OpenSmc.DataStorage.EntityFramework
{
    public class EntityFrameworkDataStorage(Action<DbContextOptionsBuilder> dbContextOptionsBuilder) : IDataStorage
    {
        private Action<ModelBuilder> modelBuilder;
        private EntityFrameworkContext Context { get; set; } 

        private Task CreateContext(CancellationToken cancellationToken)
        {
            Context = new EntityFrameworkContext(modelBuilder, dbContextOptionsBuilder);
            return Context.Database.EnsureCreatedAsync(cancellationToken);
        }

        public IQueryable<T> Query<T>() where T : class
            => Context.Set<T>().AsQueryable();


        public void Initialize(Action<ModelBuilder> builder)
            => modelBuilder = builder;

        private class Transaction(EntityFrameworkDataStorage dataStorage) : ITransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken)
                => dataStorage.CommitAsync(cancellationToken);

            public Task RevertAsync()
                => dataStorage.RevertAsync();

            public async ValueTask DisposeAsync()
                => await RevertAsync();
        }

        private async Task RevertAsync()
        {
            await Context.DisposeAsync();
            Context = null;
        }


        public async Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken)
        {
            await CreateContext(cancellationToken);
            return new Transaction(this);
        }

        public void Add<T>(IEnumerable<T> instances) where T : class
        {
            throw new NotImplementedException();
        }

        public void Update<T>(IEnumerable<T> instances) where T : class
        {
            throw new NotImplementedException();
        }

        public void Delete<T>(IEnumerable<T> instances) where T : class
        {
            throw new NotImplementedException();
        }

        public void Add<T>(IReadOnlyCollection<T> instances) where T : class
        {
            var dbSet = Context.Set<T>();
            dbSet.AddRange(instances);

        }

        public void Update<T>(IReadOnlyCollection<T> instances) where T : class
        {
            var dbSet = Context.Set<T>();
            dbSet.UpdateRange(instances);
        }

        public void Delete<T>(IReadOnlyCollection<T> instances) where T : class
        {
            var dbSet = Context.Set<T>();
            dbSet.RemoveRange(instances);
        }

        private Task CommitAsync(CancellationToken cancellationToken) =>
             Context.SaveChangesAsync(cancellationToken);

    }
}
