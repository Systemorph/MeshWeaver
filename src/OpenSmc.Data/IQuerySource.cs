namespace OpenSmc.Data
{
    public interface IQuerySource
    {
        IReadOnlyCollection<T> GetData<T>() where T : class;
    }

}