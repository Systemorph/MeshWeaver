using System.Collections.Immutable;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Models
{
    public record RowGroup : IGroup
    {
        public IImmutableList<string> Coordinates { get; init; } = ImmutableList<string>.Empty;
        public string SystemName { get; init; }
        public string DisplayName { get; init; }
        public string GrouperName { get; init; }

        public RowGroup(string systemName, string displayName, string grouperName)
        {
            SystemName = systemName;
            DisplayName = displayName;
            GrouperName = grouperName;
            Coordinates = Coordinates.Add(systemName);
        }

        public RowGroup()
        {
        }

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
