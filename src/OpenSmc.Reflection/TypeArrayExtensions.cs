namespace OpenSmc.Reflection
{
    public static class TypeArrayExtensions
    {
        public static TypeArrayKey GetKey(this ICollection<Type> types)
        {
            return new TypeArrayKey(types);
        }
    }

    public class TypeArrayKey : IEquatable<TypeArrayKey>
    {
        private readonly Type[] types;
        private readonly int hashCode;

        public TypeArrayKey(ICollection<Type> types)
        {
            if (types == null)
                throw new ArgumentNullException(nameof(types));

            this.types = new Type[types.Count];
            var i = 0;
            hashCode = 0;
            unchecked
            {
                foreach (var type in types)
                {
                    if (type == null)
                        throw new ArgumentException("Null types were present", nameof(types));
                    this.types[i] = type;
                    hashCode = hashCode * 17 ^ type.GetHashCode();
                    ++i;
                }
            }
        }

        public override string ToString()
        {
            return string.Join("|", types.Select(x => x.FullName));
        }

        public bool Equals(TypeArrayKey other)
        {
            if (ReferenceEquals(null, other))
                return false;
            if (ReferenceEquals(this, other))
                return true;
            return types.SequenceEqual(other.types);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
                return false;
            if (ReferenceEquals(this, obj))
                return true;
            if (obj.GetType() != GetType())
                return false;
            return Equals((TypeArrayKey)obj);
        }

        public override int GetHashCode()
        {
            return hashCode;
        }
    }
}