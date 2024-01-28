using System.Collections.Immutable;

namespace OpenSmc.Pivot.Models.Interfaces
{
    public interface IItemWithCoordinates
    {
        // why init?
        string SystemName { get; init; }
        string DisplayName { get; init; }
        IImmutableList<string> Coordinates { get; init; }
        string GrouperName { get; init; }
    }
}