using System.Collections.Immutable;

namespace OpenSmc.Pivot.Models.Interfaces;

public interface IItemWithCoordinates
{
    object SystemName { get; init; }
    string DisplayName { get; init; }
    ImmutableList<object> Coordinates { get; init; }
    object GrouperName { get; init; }
}

public abstract record ItemWithCoordinates() : IItemWithCoordinates
{
    public object SystemName { get; init; }
    public string DisplayName { get; init; }
    public ImmutableList<object> Coordinates { get; init; } = ImmutableList<object>.Empty;

    public object GrouperName { get; init; }
}
