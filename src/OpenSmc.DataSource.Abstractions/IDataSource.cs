using OpenSmc.Data;

namespace OpenSmc.DataSource.Abstractions
{
    public interface IDataSource : IQuerySource
    {
        Task UpdateAsync<T>(IEnumerable<T> instances, UpdateOptions options);

        Task UpdateAsync<T>(T instance)
            => UpdateAsync(instance, new());
        Task UpdateAsync<T>(T instance, UpdateOptions options)
            => UpdateAsync<T>(new[]{instance}, options);
        Task DeleteAsync<T>(IEnumerable<T> instances);
        Task DeleteAsync<T>(T instance);
        Task CommitAsync();
    }

}