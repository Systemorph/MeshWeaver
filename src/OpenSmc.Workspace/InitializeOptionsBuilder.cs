using System.Collections.Immutable;
using OpenSmc.DataSource.Abstractions;

namespace OpenSmc.Workspace;

public record InitializeOptionsBuilder
{
    private IQuerySource QuerySource { get; init; }
    private ImmutableHashSet<Type> DisabledInitialization { get; init; }
    private ImmutableDictionary<Type, Delegate> InitFunctions { get; init; }

    public static InitializeOptionsBuilder Empty = new();

    private InitializeOptionsBuilder()
    {
        DisabledInitialization = ImmutableHashSet.Create<Type>();
        InitFunctions = ImmutableDictionary.Create<Type, Delegate>();
    }

    public InitializeOptionsBuilder FromSource(IQuerySource source)
    {
        return this with { QuerySource = source };
    }

    public InitializeOptionsBuilder FromFunction<T>(Func<IAsyncEnumerable<T>> func)
    {
        return this with { InitFunctions = InitFunctions.SetItem(typeof(T), func) };
    }

    public InitializeOptionsBuilder DisableInitialization<T>() => DisableInitialization(typeof(T));

    public InitializeOptionsBuilder DisableInitialization(Type type)
    {
        return this with { DisabledInitialization = DisabledInitialization.Add(type) };
    }

    public InitializeOptionsBuilder EnableInitialization<T>() => EnableInitialization(typeof(T));

    public InitializeOptionsBuilder EnableInitialization(Type type)
    {
        return this with { DisabledInitialization = DisabledInitialization.Remove(type) };
    }

    public InitializeOptions GetOptions() => new(QuerySource, DisabledInitialization, InitFunctions);
}

public record InitializeOptions(IQuerySource QuerySource, 
                                ImmutableHashSet<Type> DisabledInitialization, 
                                ImmutableDictionary<Type, Delegate> InitFunctions);