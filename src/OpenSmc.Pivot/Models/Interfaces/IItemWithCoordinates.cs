using System.Collections.Immutable;

namespace OpenSmc.Pivot.Models.Interfaces;

public interface IItemWithCoordinates
{
    string SystemName { get; init; }
    string DisplayName { get; init; }
    ImmutableList<string> Coordinates { get; init; }
    string GrouperName { get; init; }
}

public abstract record ItemWithCoordinates() : IItemWithCoordinates
{
    public string SystemName { get; init; }
    public string DisplayName { get; init; }
    public ImmutableList<string> Coordinates { get; init; } = ImmutableList<string>.Empty;

    public string GrouperName { get; init; }
}
