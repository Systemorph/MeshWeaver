using System.Collections.Immutable;
using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Models
{
    public record ColumnGroup : Column, IGroup
    {
        public ImmutableList<Column> Children { get; init; } = ImmutableList<Column>.Empty;

        public ColumnGroup() { }

        public ColumnGroup(IGroup group)
        {
            Id = group.Id;
            DisplayName = group.DisplayName;
            GrouperName = group.GrouperName;
            Coordinates = group.Coordinates;
        }

        public ColumnGroup(object id, string displayName, string grouperName)
        {
            Id = id;
            DisplayName = displayName;
            GrouperName = grouperName;
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
            if (Id != other.Id || DisplayName != other.DisplayName)
                return false;
            return Coordinates.SequenceEqual(other.Coordinates);
        }

        public override int GetHashCode()
        {
            var hashCode = new HashCode();
            hashCode.Add(Id);
            hashCode.Add(DisplayName);
            foreach (var coordinate in Coordinates)
            {
                hashCode.Add(coordinate);
            }

            return hashCode.ToHashCode();
        }
    }
}
