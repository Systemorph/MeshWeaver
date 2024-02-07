namespace OpenSmc.Data;

public abstract record TypeConfiguration()
{
    public abstract Task<IEnumerable<object>> DoInitialize();
}

public record TypeConfiguration<T>(
    Func<Task<IReadOnlyCollection<T>>> Initialize,
    Func<IReadOnlyCollection<T>, Task> Save,
    Func<IReadOnlyCollection<T>, Task> Delete) : TypeConfiguration
{
    public override async Task<IEnumerable<object>> DoInitialize()
    {
        return (await Initialize()).Cast<object>().ToArray();
    }
}