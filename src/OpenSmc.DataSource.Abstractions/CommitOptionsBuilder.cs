using System.Collections.Immutable;

namespace OpenSmc.DataSource.Abstractions;

public record CommitOptionsBuilder
{
    private ImmutableHashSet<Type> TypesToProcessInSnapshotMode { get; init; }

    public static CommitOptionsBuilder Empty { get; } = new ();

    private CommitOptionsBuilder()
    {
        TypesToProcessInSnapshotMode = ImmutableHashSet.Create<Type>();
    }

    public CommitOptionsBuilder SnapshotMode<T>() => SnapshotMode(typeof(T));

    public CommitOptionsBuilder SnapshotMode(Type type)
    {
        return this with { TypesToProcessInSnapshotMode = TypesToProcessInSnapshotMode.Add(type) };
    }

    public CommitOptions GetOptions() => new(TypesToProcessInSnapshotMode);
}

public record CommitOptions(ImmutableHashSet<Type> TypesToProcessInSnapshotMode);