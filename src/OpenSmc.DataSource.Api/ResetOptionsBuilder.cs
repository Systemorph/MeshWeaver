using System;
using System.Collections.Immutable;

namespace OpenSmc.DataSource.Api;

public record ResetOptionsBuilder
{
    private ImmutableHashSet<Type> TypesToReset { get; init; }
    private bool InitializationRulesReset { get; init; }
    private bool CurrentPartitionsReset { get; init; }

    public static ResetOptionsBuilder Empty { get; } = new();

    private ResetOptionsBuilder()
    {
        TypesToReset = ImmutableHashSet.Create<Type>();
    }

    public ResetOptionsBuilder ResetType<T>() => ResetType(typeof(T));

    public ResetOptionsBuilder ResetType(Type type)
    {
        return this with { TypesToReset = TypesToReset.Add(type) };
    }

    public ResetOptionsBuilder ResetInitializationRules()
    {
        return this with { InitializationRulesReset = true };
    }

    public ResetOptionsBuilder ResetCurrentPartitions()
    {
        return this with { CurrentPartitionsReset = true };
    }

    public ResetOptions GetOptions() => new(TypesToReset, InitializationRulesReset, CurrentPartitionsReset);
}

public record ResetOptions(ImmutableHashSet<Type> TypesToReset, bool InitializationRulesReset, bool CurrentPartitionsReset)
{
    public ResetOptions() : this(ImmutableHashSet<Type>.Empty, false, false)
    {
    }
}