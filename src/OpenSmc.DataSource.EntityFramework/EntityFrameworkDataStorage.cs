using Microsoft.EntityFrameworkCore;
using OpenSmc.DataSource.Abstractions;

namespace OpenSmc.DataStorage.EntityFramework
{
    public class EntityFrameworkDataStorage(Action<ModelBuilder> modelBuilder, Action<DbContextOptionsBuilder> dbContextOptionsBuilder) : IDataStorage
    { 
        private EntityFrameworkContext Context { get; set; } 

        private Task CreateContext()
        {
            Context = new EntityFrameworkContext(modelBuilder, dbContextOptionsBuilder);
            return Context.Database.EnsureCreatedAsync();
        }

        public IQueryable<T> Query<T>() where T : class
            => Context.Set<T>().AsQueryable();


        private class Transaction(EntityFrameworkDataStorage dataStorage) : ITransaction
        {
            public Task CommitAsync()
                => dataStorage.CommitAsync();

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

        public async Task<ITransaction> StartTransactionAsync()
        {
            await CreateContext();
            return new Transaction(this);
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

        private Task CommitAsync() =>
             Context.SaveChangesAsync();

    }
}
