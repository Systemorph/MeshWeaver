using OpenSmc.Data;
using OpenSmc.DataSource.Abstractions;

namespace OpenSmc.DataSource.EntityFramework
{
    public class EntityFrameworkDataSource : IDataSource
    {
        private EntityFrameworkContext context;

        public IQueryable<T> Query<T>() where T : class
        {
            throw new NotImplementedException();
        }

        public Task UpdateAsync<T>(IEnumerable<T> instances, UpdateOptions options)
        {
            throw new NotImplementedException();
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
