using System.Collections.Immutable;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Models
{
    public record RowGroup : IGroup
    {
        public ImmutableList<object> Coordinates { get; init; } = ImmutableList<object>.Empty;
        public object SystemName { get; init; }
        public string DisplayName { get; init; }
        public object GrouperName { get; init; }

        public RowGroup(object systemName, string displayName, object grouperName)
        {
            SystemName = systemName;
            DisplayName = displayName;
            GrouperName = grouperName;
            Coordinates = Coordinates.Add(systemName);
        }

        public RowGroup() { }

        public virtual bool Equals(RowGroup other)
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
