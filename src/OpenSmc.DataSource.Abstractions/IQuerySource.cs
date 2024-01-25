namespace OpenSmc.DataSource.Abstractions
{
    public interface IQuerySource
    {
        IQueryable<T> Query<T>();
    }

}