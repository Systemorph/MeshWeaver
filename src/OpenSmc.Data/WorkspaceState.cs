namespace OpenSmc.Data;

public record WorkspaceState(IDataSource DataSource)
{

    public IReadOnlyDictionary<string, IReadOnlyDictionary<object, object>> GetData()
        => DataSource.GetData();



    private IReadOnlyDictionary<object, object> GetItemsByKey(Type type)
        => DataSource.GetTypeSource(type).GetData();

    public T GetItem<T>(object id) where T : class
    {
        return (T)GetItemsByKey(typeof(T)).GetValueOrDefault(id);
    }

    public void Change(DataChangeRequest request)
    {
        DataSource.Change(request);
    }
}

