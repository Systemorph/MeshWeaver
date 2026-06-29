#nullable enable
namespace MeshWeaver.Reflection
{
    /// <summary>
    /// Provides extension methods for turning a collection of types into a value-comparable key.
    /// </summary>
    public static class TypeArrayExtensions
    {
        /// <summary>
        /// Creates a <see cref="TypeArrayKey"/> that can be used as a dictionary key or equality token for the given <paramref name="types"/>.
        /// </summary>
        /// <param name="types">The collection of types to build the key from.</param>
        /// <returns>A value-comparable key over the ordered types.</returns>
        public static TypeArrayKey GetKey(this ICollection<Type> types)
        {
            return new TypeArrayKey(types);
        }
    }

    /// <summary>
    /// An immutable, value-equatable key over an ordered array of types, suitable for use as a dictionary key.
    /// </summary>
    public class TypeArrayKey : IEquatable<TypeArrayKey>
    {
        private readonly Type[] types;
        private readonly int hashCode;

        /// <summary>
        /// Initializes a new <see cref="TypeArrayKey"/> capturing the given <paramref name="types"/> and precomputing its hash code.
        /// </summary>
        /// <param name="types">The ordered collection of non-null types to key on.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="types"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="types"/> contains a <c>null</c> element.</exception>
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

        /// <summary>
        /// Returns the full type names joined by <c>|</c>, e.g. <c>System.Int32|System.String</c>.
        /// </summary>
        /// <returns>A pipe-delimited string of the captured type names.</returns>
        public override string ToString()
        {
            return string.Join("|", types.Select(x => x.FullName));
        }

        /// <summary>
        /// Determines whether this key equals <paramref name="other"/> by comparing the ordered type sequences.
        /// </summary>
        /// <param name="other">The key to compare with.</param>
        /// <returns><c>true</c> if both keys contain the same types in the same order.</returns>
        public bool Equals(TypeArrayKey? other)
        {
            if (ReferenceEquals(null, other))
                return false;
            if (ReferenceEquals(this, other))
                return true;
            return types.SequenceEqual(other.types);
        }

        /// <summary>
        /// Determines whether <paramref name="obj"/> is a <see cref="TypeArrayKey"/> equal to this one.
        /// </summary>
        /// <param name="obj">The object to compare with.</param>
        /// <returns><c>true</c> if <paramref name="obj"/> is an equal key.</returns>
        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj))
                return false;
            if (ReferenceEquals(this, obj))
                return true;
            if (obj.GetType() != GetType())
                return false;
            return Equals((TypeArrayKey)obj);
        }

        /// <summary>
        /// Returns the precomputed hash code, consistent with <c>Equals</c>.
        /// </summary>
        /// <returns>The hash code for this key.</returns>
        public override int GetHashCode()
        {
            return hashCode;
        }
    }
}