using System.Collections.Immutable;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Models
{
    public record RowGroup : IGroup
    {
        public ImmutableList<object> Coordinates { get; init; } = ImmutableList<object>.Empty;
        public object Id { get; init; }
        public string DisplayName { get; init; }
        public object GrouperId { get; init; }

        public RowGroup(object systemName, string displayName, object grouperName)
        {
            Id = systemName;
            DisplayName = displayName;
            GrouperId = grouperName;
            Coordinates = Coordinates.Add(systemName);
        }

        public RowGroup() { }

        public virtual bool Equals(RowGroup other)
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
