using System.Collections.Immutable;
using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Models
{
    public record RowGroup : IGroup
    {
        public ImmutableList<object> Coordinates { get; init; } = ImmutableList<object>.Empty;
        public object? Id { get; init; }
        public string? DisplayName { get; init; }
        public string? GrouperName { get; init; }

        public RowGroup(object id, string displayName, string grouperName)
        {
            Id = id;
            DisplayName = displayName;
            GrouperName = grouperName;
            Coordinates = Coordinates.Add(id);
        }

        public RowGroup() { }

        public virtual bool Equals(RowGroup? other)
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
