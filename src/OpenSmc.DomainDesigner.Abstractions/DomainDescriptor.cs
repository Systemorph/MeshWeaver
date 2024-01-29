using OpenSmc.Collections;

namespace OpenSmc.DomainDesigner.Abstractions
{
    public record DomainDescriptor
    {
        public string DomainName { get; init; }

        public HashSet<Type> Types { get; init; }

        public virtual bool Equals(DomainDescriptor other)
        {
            if (ReferenceEquals(null, other))
                return false;
            if (ReferenceEquals(this, other))
                return true;
            return DomainName == other.DomainName && Types.NullableSequenceEquals(other.Types);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(DomainName, Types);
        }
    }
}
