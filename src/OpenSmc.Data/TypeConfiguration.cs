using System.Collections.Generic;

namespace OpenSmc.Data;

public abstract record TypeConfiguration()
{
    public abstract Task<IEnumerable<object>> DoInitialize();
}

public record TypeConfiguration<T>(
    Func<Task<IReadOnlyCollection<T>>> Initialize,
    Func<IEnumerable<T>, Task> Save,
    Func<IEnumerable<T>, Task> Delete) : TypeConfiguration
{
    public override async Task<IEnumerable<object>> DoInitialize()
    {
        return (await Initialize()).Cast<object>().ToArray();
    }
}