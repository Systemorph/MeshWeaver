using System.Collections.Immutable;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Models
{
    public record ColumnGroup : Column, IGroup
    {
        public IImmutableList<Column> Children { get; init; } = ImmutableList<Column>.Empty;

        public ColumnGroup() { }

        public ColumnGroup(IGroup group)
        {
            SystemName = group.SystemName.ToString();
            DisplayName = group.DisplayName;
            GrouperName = group.GrouperName;
            Coordinates = group.Coordinates;
        }

        public ColumnGroup(object id, string displayName, object grouperId)
        {
            SystemName = id;
            DisplayName = displayName;
            GrouperName = grouperId;
            Coordinates = Coordinates.Add(id);
        }

        public ColumnGroup AddChildren(IEnumerable<Column> children)
        {
            return this with { Children = Children.AddRange(children) };
        }

        public virtual bool Equals(ColumnGroup other)
        {
            if (ReferenceEquals(null, other))
                return false;
            if (ReferenceEquals(this, other))
                return true;
            if (SystemName != other.SystemName || DisplayName != other.DisplayName)
                return false;
            return Coordinates.SequenceEqual(other.Coordinates);
        }

        public override int GetHashCode()
        {
            var hashCode = new HashCode();
            hashCode.Add(SystemName);
            hashCode.Add(DisplayName);
            foreach (var coordinate in Coordinates)
            {
                hashCode.Add(coordinate);
            }

            return hashCode.ToHashCode();
        }
    }
}
