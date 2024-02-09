using Microsoft.EntityFrameworkCore;
using OpenSmc.Data;
using OpenSmc.DataSource.Abstractions;

namespace OpenSmc.DataSource.EntityFramework
{
    public class EntityFrameworkDataSource(Action<ModelBuilder> modelBuilder) : IDataSource
    { 
        private EntityFrameworkContext Context { get; } = new(modelBuilder);

        public IQueryable<T> Query<T>() where T : class
            => Context.Set<T>().AsQueryable();

        public Task UpdateAsync<T>(IEnumerable<T> instances, UpdateOptions options) where T : class
        {
            return Context.Set<T>().Update(instances);
        }

        public Task DeleteAsync<T>(IEnumerable<T> instances)
        {
            throw new NotImplementedException();
        }

        public Task DeleteAsync<T>(T instance)
        {
            throw new NotImplementedException();
        }

        public Task CommitAsync()
        {
            throw new NotImplementedException();
        }
    }
}
