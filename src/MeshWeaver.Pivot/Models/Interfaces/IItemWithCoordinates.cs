using System.Collections.Immutable;

namespace MeshWeaver.Pivot.Models.Interfaces;

public interface IItemWithCoordinates
{
    object? Id { get; init; }
    string DisplayName { get; init; }
    ImmutableList<object> Coordinates { get; init; }
    string GrouperName { get; init; }
}

public abstract record ItemWithCoordinates() : IItemWithCoordinates
{
    public object Id { get; init; } = null!;
    public string DisplayName { get; init; } = null!;
    public ImmutableList<object> Coordinates { get; init; } = ImmutableList<object>.Empty;

    public string GrouperName { get; init; } = null!;
}
