namespace OpenSmc.Data
{
    public interface IQuerySource
    {
        IQueryable<T> Query<T>() where T:class;
    }

}