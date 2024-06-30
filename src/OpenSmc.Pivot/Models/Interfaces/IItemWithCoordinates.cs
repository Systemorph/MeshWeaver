using System.Collections.Immutable;

namespace OpenSmc.Pivot.Models.Interfaces;

public interface IItemWithCoordinates
{
    object Id { get; init; }
    string DisplayName { get; init; }
    ImmutableList<object> Coordinates { get; init; }
    string GrouperName { get; init; }
}

public abstract record ItemWithCoordinates() : IItemWithCoordinates
{
    public object Id { get; init; }
    public string DisplayName { get; init; }
    public ImmutableList<object> Coordinates { get; init; } = ImmutableList<object>.Empty;

    public string GrouperName { get; init; }
}
