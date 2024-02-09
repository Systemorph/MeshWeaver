namespace OpenSmc.Data;

public abstract record TypeConfiguration()
{
    public abstract Task<IEnumerable<object>> DoInitialize();
    public abstract object GetKey(object instance);
    internal Func<IEnumerable<object>, Task> DeleteByIds { get; init; }
}

public record TypeConfiguration<T> : TypeConfiguration
{

    public override async Task<IEnumerable<object>> DoInitialize()
    {
        return (await Initialization()).Cast<object>().ToArray();
    }

    public override object GetKey(object instance)
        => Key((T)instance);

    internal Func<T, object> Key { get; init; }

    public TypeConfiguration<T> WithKey(Func<T, object> key)
        => this with { Key = key };
    internal Func<Task<IReadOnlyCollection<T>>> Initialization { get; init; }
    public TypeConfiguration<T> WithInitialization(Func<Task<IReadOnlyCollection<T>>> initialization)
        => this with { Initialization = initialization };
    internal Func<IEnumerable<T>, Task> Save { get; init; }

    public TypeConfiguration<T> WithSave(Func<IEnumerable<T>, Task> save) => this with { Save = save };

    internal Func<IEnumerable<T>, Task> Delete { get; init; }
    public TypeConfiguration<T> WithDelete(Func<IEnumerable<T>, Task> delete) => this with { Delete = delete };

    public TypeConfiguration<T> WithDeleteById(Func<IEnumerable<object>, Task> delete) => this with { DeleteByIds = delete };
}
