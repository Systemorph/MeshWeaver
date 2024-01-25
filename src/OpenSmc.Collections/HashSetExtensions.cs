using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace OpenSmc.Collections
{
    public static class HashSetExtensions
    {
        public static HashSet<T> Clone<T>(this HashSet<T> original)
        {
            return original is ICloneable cloneable
                       ? (HashSet<T>)cloneable.Clone()
                       : new HashSet<T>(original, original.Comparer);
        }

        internal static IEnumerable<T> IntersectHashed<T>(this IEnumerable<T> enumerable, params IEnumerable<T>[] toIntersectWith)
        {
            if (toIntersectWith.Length == 0)
                throw new ArgumentException("No collections to intersect with.");
            var list = new List<ICollection<T>>(toIntersectWith.Length);
            foreach (var enumerable1 in toIntersectWith)
            {
                var coll = enumerable1 as ICollection<T> ?? new HashSet<T>(enumerable1);
                list.Add(coll);
            }
            var orderedIndex = Enumerable.Range(0, list.Count).OrderBy(x => list[x].Count).ToList();

            if (enumerable == null)
            {
                enumerable = list[orderedIndex[0]];
                orderedIndex.RemoveAt(0);
            }
            var ret = orderedIndex.Select(x => list[x]).Aggregate(enumerable, (c1, c2) => c1.CollectiveAction(c2, (x, y) => x.IntersectWith(y), true));
            return ret;
        }


        [SuppressMessage("ReSharper", "PossibleMultipleEnumeration")]
        private static IEnumerable<T> CollectiveAction<T>(this IEnumerable<T> c1, IEnumerable<T> c2, Action<ISet<T>, IEnumerable<T>> action, bool commutative = false)
        {

            var sorted = c1 as SortedSet<T>;
            if (sorted != null)
            {
                action(sorted, c2);
                return sorted;
            }

            var coll = CastOrClone(c1);
            ISet<T> coll2;
            if (coll != null)
            {
                //TODO: For Union, it must be the other way round. ==> re-consider the algorithm.
                //coll2 = c2 as ISet<T>;
                //if (coll2 != null && coll2.Count < coll.Count)
                //{
                //    action(coll2, coll);
                //    return coll2;
                //}
                action(coll, c2);
                return coll;
            }

            // c1 is not ICollection
            coll2 = CastOrClone(c2);
            if (coll2 != null && commutative)
            {
                action(coll2, c1);
                return coll2;
            }

            //both are not ICollection
            // build result set
            var ret = new HashSet<T>(c1);
            action(ret, c2);
            return ret;
        }

        private static ISet<T> CastOrClone<T>(IEnumerable<T> c1)
        {
            var cached = c1 as CachedHashSet<T>;
            if (cached == null)
                return c1 as ISet<T>;
            return cached.Clone();
        }

        public static IEnumerable<T> UnionHashed<T>(this IEnumerable<T> c1, IEnumerable<T> c2)
        {
            var ret = c1.CollectiveAction(c2, (x, y) => x.UnionWith(y), true);
            return ret;
        }

        public static IEnumerable<T> UnionAllHashed<T>(this IEnumerable<IEnumerable<T>> source)
        {
            var ret = source.Aggregate(Enumerable.Empty<T>(), (current, coll) => current.UnionHashed(coll));
            return ret;
        }


        public static IEnumerable<T> ExceptHashed<T>(this IEnumerable<T> c1, IEnumerable<T> c2)
        {
            var ret = c1.CollectiveAction(c2, (x, y) => x.ExceptWith(y));
            return ret;
        }

        private static IEnumerable<T> IntersectMany<T>(this IEnumerable<T> c1, ICollection<HashSet<T>> c2)
        {
            var cached = c1 as CachedHashSet<T>;

            var hs = cached != null
                ? cached.Clone()
                : (c1 as HashSet<T> ?? new HashSet<T>(c1));
            if (c2.Count == 1)
                hs.IntersectWith(c2.Single());
            else
                foreach (var el in hs.ChangeResistentEnumerable())
                    if (c2.All(x => !x.Contains(el)))
                        hs.Remove(el);
            return hs;
        }

        public static IEnumerable<T> IntersectMany<T>(this IEnumerable<T> enumerable, params IEnumerable<IEnumerable<T>>[] collections)
        {
            if (collections.Length == 0)
                throw new ArgumentException("Empty collections were passed", nameof(collections));

            // treating special cases
            if (enumerable == null && collections.Length == 1)
            {
                var subColl = collections[0] as ICollection<IEnumerable<T>>;
                if (subColl != null && subColl.Count == 1)
                    return subColl.Single();
                return collections[0].SelectMany(x => x.Select(y => y));
            }

            var parsed = new ICollection<HashSet<T>>[collections.Length];
            for (int j = 0; j < collections.Length; j++)
            {
                var cast = collections[j] as ICollection<IEnumerable<T>>;
                var coll = parsed[j] = cast != null ? new List<HashSet<T>>(cast.Count) : new List<HashSet<T>>();

                foreach (var collection in collections[j])
                {
                    var hs = collection as HashSet<T> ?? new HashSet<T>(collection);
                    coll.Add(hs);
                }
            }

            var nCollections = parsed.Select(x => x.Count).ToArray();

            var order = Enumerable.Range(0, collections.Length).OrderBy(x => nCollections[x]).ToList();
            if (enumerable == null)
            {
                var nElements = parsed.Select(x => x.Sum(y => y.Count)).ToArray();
                var index = Enumerable.Range(0, collections.Length).OrderBy(x => nElements[x]).First();
                order.Remove(index);
                order.Insert(0, index);
            }

            if (enumerable != null)
                return parsed.Aggregate(enumerable, (c1, c2) => c1.IntersectMany(c2));

            var baseColl = (IEnumerable<IEnumerable<T>>)parsed[order[0]];
            return order.Skip(1)
                      .Select(x => parsed[x])
                      .Aggregate(baseColl, (x, y) => x.Select(z => (ICollection<T>)z.IntersectMany(y)).ToArray())
                      .UnionAllHashed();
        }

        private static IEnumerable<T> ExceptMany<T>(this IEnumerable<T> c1, ICollection<HashSet<T>> c2)
        {
            var cached = c1 as CachedHashSet<T>;

            var hs = cached != null
                ? cached.Clone()
                : (c1 as HashSet<T> ?? new HashSet<T>(c1));
            if (c2.Count == 1)
                hs.ExceptWith(c2.Single());
            else
                // Probably is only efficient when c2 is a substantial portion of c1, else taking first the union over c2 is likely to be faster,
                // unless we have a lot of collection resizings.. anyway the ExceptMany case still needs to be tuned on large collections (TJ)
                foreach (var el in hs.ChangeResistentEnumerable())
                    if (c2.Any(x => x.Contains(el)))
                        hs.Remove(el);
            return hs;
        }

        public static IEnumerable<T> ExceptMany<T>(this IEnumerable<T> enumerable, params IEnumerable<IEnumerable<T>>[] collections)
        {
            if (enumerable == null)
                throw new ArgumentNullException(nameof(enumerable));
            // ReSharper disable PossibleMultipleEnumeration
            if (collections.Length == 0 || !enumerable.Any())
                return enumerable;
            // ReSharper enable PossibleMultipleEnumeration
            var parsed = new ICollection<HashSet<T>>[collections.Length];
            for (int j = 0; j < collections.Length; j++)
            {
                var cast = collections[j] as ICollection<IEnumerable<T>>;
                var coll = parsed[j] = cast != null ? new List<HashSet<T>>(cast.Count) : new List<HashSet<T>>();

                foreach (var collection in collections[j])
                {
                    var hs = collection as HashSet<T> ?? new HashSet<T>(collection);
                    coll.Add(hs);
                }
            }

            return parsed.Aggregate(enumerable, (c1, c2) => c1.ExceptMany(c2));
        }

        internal static bool IsGrouping(this Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IGrouping<,>);
        }

        [SuppressMessage("ReSharper", "StaticMemberInGenericType", Justification = "That's the point of this - to have multiple copies")]
        [SuppressMessage("ReSharper", "UnusedMember.Local")]
        [SuppressMessage("ReSharper", "InconsistentNaming")]
        private static class Fields<T>
        {
            public static readonly FieldInfo freeList = GetFieldInfo("m_freeList");
            public static readonly FieldInfo buckets = GetFieldInfo("m_buckets");
            public static readonly FieldInfo slots = GetFieldInfo("m_slots");
            public static readonly FieldInfo count = GetFieldInfo("m_count");
            public static readonly FieldInfo lastIndex = GetFieldInfo("m_lastIndex");
            public static readonly FieldInfo version = GetFieldInfo("m_version");
            public static readonly FieldInfo comparer = GetFieldInfo("m_comparer");

            private static FieldInfo GetFieldInfo(string name)
            {
                return typeof(HashSet<T>).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            }
        }

        // TODO V10: Consider getting rid of this (2021.10.15, Dmitrii Neliubin)
        public static IEnumerable<T> ChangeResistentEnumerable<T>(this HashSet<T> hs)
        {
            return new ChangeResistentEnumerator<T>(hs);
        }

        private class ChangeResistentEnumerator<T> : IEnumerable<T>, IEnumerator<T>
        {
            private int index;
            private readonly int lastIndex;
            private readonly Array slots;

            private static readonly Func<HashSet<T>, int> GetLastIndex = GetLastIndexFunc();
            private static readonly Func<HashSet<T>, Array> GetSlots = GetSlotsFunc();
            [SuppressMessage("ReSharper", "StaticMemberInGenericType", Justification = "This actually depends on T")]
            private static readonly Func<Array, int, int> GetSlotHashCode = GetSlotHashCodeFunc();
            private static readonly Func<Array, int, T> GetElement = GetElementFunc();

            public ChangeResistentEnumerator(HashSet<T> set)
            {
                lastIndex = GetLastIndex(set);
                slots = GetSlots(set);
            }

            private static Func<Array, int, T> GetElementFunc()
            {
                var arrayParam = Expression.Parameter(typeof(Array));
                var index = Expression.Parameter(typeof(int));
                var el = Expression.ArrayIndex(Expression.Convert(arrayParam, Fields<T>.slots.FieldType), index);
                return Expression.Lambda<Func<Array, int, T>>(Expression.Field(el, "value"), arrayParam, index).Compile();
            }

            private static Func<Array, int, int> GetSlotHashCodeFunc()
            {
                var arrayParam = Expression.Parameter(typeof(Array));
                var index = Expression.Parameter(typeof(int));
                var el = Expression.ArrayIndex(Expression.Convert(arrayParam, Fields<T>.slots.FieldType), index);
                return Expression.Lambda<Func<Array, int, int>>(Expression.Field(el, "hashCode"), arrayParam, index).Compile();
            }


            private static Func<HashSet<T>, Array> GetSlotsFunc()
            {
                var prm = Expression.Parameter(typeof(HashSet<T>));
                return Expression.Lambda<Func<HashSet<T>, Array>>(Expression.Field(prm, Fields<T>.slots), prm).Compile();
            }

            private static Func<HashSet<T>, int> GetLastIndexFunc()
            {
                var prm = Expression.Parameter(typeof(HashSet<T>));
                return Expression.Lambda<Func<HashSet<T>, int>>(Expression.Field(prm, Fields<T>.lastIndex), prm).Compile();
            }


            public IEnumerator<T> GetEnumerator()
            {
                Reset();
                return this;
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                while (index < lastIndex)
                {
                    if (GetSlotHashCode(slots, index) > 0)
                    {
                        Current = GetElement(slots, index++);
                        return true;
                    }
                    index++;
                }
                return false;
            }


            public void Reset()
            {
                index = 0;
                Current = default(T);
            }

            public T Current { get; private set; }

            object IEnumerator.Current
            {
                get { return Current; }
            }
        }


    }
}
