namespace OpenSmc.Data
{
    public interface IReadOnlyWorkspace
    {
        IReadOnlyCollection<T> GetData<T>() where T : class;
    }
}