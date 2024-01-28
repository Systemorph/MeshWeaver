using System.Collections.Immutable;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Models
{
    public record Column : IItemWithCoordinates
    {
        public Column()
        {
        }

        public Column(string systemName, string displayName)
        {
            SystemName = systemName;
            DisplayName = displayName;
            Coordinates = Coordinates.Add(systemName);
        }

        public IImmutableList<string> Coordinates { get; init; } = ImmutableList<string>.Empty;

        public string SystemName { get; init; }
        public string DisplayName { get; init; }
        public string GrouperName { get; init; } = PivotConst.ColumnGrouperName;
    }
}