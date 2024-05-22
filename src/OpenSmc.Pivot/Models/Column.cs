using System.Collections.Immutable;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Models
{
    public record Column : ItemWithCoordinates
    {
        public Column()
        {
            GrouperName = PivotConst.ColumnGrouperName;
        }

        public Column(string id, string displayName)
            : this()
        {
            SystemName = id;
            DisplayName = displayName;
            Coordinates = ImmutableList<object>.Empty.Add(id);
        }
    }
}
