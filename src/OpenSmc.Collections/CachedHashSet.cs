namespace OpenSmc.Collections
{
    public class CachedHashSet<T> : HashSet<T>, ICloneable
    {
        public CachedHashSet()
        {
        }

        public CachedHashSet(IEnumerable<T> collection) 
            : base(collection)
        {
        }

        public CachedHashSet(IEnumerable<T> collection, IEqualityComparer<T> comparer)
            : base(collection, comparer)
        {
        }

        object ICloneable.Clone() => new CachedHashSet<T>(this, Comparer);
    }
}