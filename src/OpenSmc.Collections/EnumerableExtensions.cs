using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace OpenSmc.Collections
{
    public static class EnumerableExtensions
    {
        public static IEnumerable<List<T>> ToChunks<T>(this IEnumerable<T> items, int chunkSize)
        {
            if (chunkSize <= 0)
                throw new ArgumentException("Chunk size must have positive value", nameof(chunkSize));

            if (items == null)
                yield break;

            var chunk = new List<T>(chunkSize);
            foreach (T item in items)
            {
                chunk.Add(item);
                if (chunk.Count == chunkSize)
                {
                    yield return chunk;
                    chunk = new List<T>(chunkSize);
                }
            }

            if (chunk.Count > 0)
                yield return chunk;
        }

        public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, Func<TKey, TValue> factory)
        {
            switch (dictionary)
            {
                case null:
                    throw new ArgumentNullException(nameof(dictionary));
                case ThreadSafeDictionary<TKey, TValue> threadSafeDictionary:
                    return threadSafeDictionary.GetOrAdd(key, factory);
                case ConcurrentDictionary<TKey, TValue> concurrentDictionary:
                    return concurrentDictionary.GetOrAdd(key, factory);
            }

            if (dictionary.TryGetValue(key, out var value))
                return value;

            value = factory(key);
            dictionary.Add(key, value);
            return value;
        }

        //public static HashSet<T> ToHashSet<T>(this IEnumerable<T> enumerable, IEqualityComparer<T> comparer = null)
        //{
        //    if (enumerable == null)
        //        throw new ArgumentNullException(nameof(enumerable));

        //    return comparer == null
        //                   ? new HashSet<T>(enumerable)
        //                   : new HashSet<T>(enumerable, comparer);
        //}

        /// <summary>
        /// UNTESTED!
        /// </summary>
        public static bool EquivalentTo<T>(this IEnumerable<T> enumerable, IEnumerable<T> other, IEqualityComparer<T> instanceComparer = null)
        {
            if (enumerable == null)
                throw new ArgumentNullException(nameof(enumerable));
            if (other == null)
                throw new ArgumentNullException(nameof(other));

            var comparer = GetEnumerableComparer(instanceComparer);
            return comparer.Equals(enumerable, other);
        }

        public static IEqualityComparer<IEnumerable<T>> GetEnumerableComparer<T>(IEqualityComparer<T> instanceComparer = null)
        {
            return new MultiSetComparer<T>(instanceComparer);
        }

        /// <remarks>taken from http://stackoverflow.com/a/3790621 which is apparently taken from mstest</remarks>
        private class MultiSetComparer<T> : IEqualityComparer<IEnumerable<T>>
        {
            private readonly IEqualityComparer<T> instanceComparer;

            public MultiSetComparer(IEqualityComparer<T> instanceComparer)
            {
                this.instanceComparer = instanceComparer ?? EqualityComparer<T>.Default;
            }

            public bool Equals(IEnumerable<T> first, IEnumerable<T> second)
            {
                if (first == null)
                    return second == null;

                if (second == null)
                    return false;

                if (ReferenceEquals(first, second))
                    return true;

                var firstCollection = first as ICollection<T>;
                var secondCollection = second as ICollection<T>;
                if (firstCollection != null && secondCollection != null)
                {
                    if (firstCollection.Count != secondCollection.Count)
                        return false;

                    if (firstCollection.Count == 0)
                        return true;
                }

                return !HaveMismatchedElement(first, second);
            }

            private bool HaveMismatchedElement(IEnumerable<T> first, IEnumerable<T> second)
            {
                int firstCount;
                int secondCount;

                var firstElementCounts = GetElementCounts(first, out firstCount);
                var secondElementCounts = GetElementCounts(second, out secondCount);

                if (firstCount != secondCount)
                    return true;

                foreach (var kvp in firstElementCounts)
                {
                    firstCount = kvp.Value;
                    secondElementCounts.TryGetValue(kvp.Key, out secondCount);

                    if (firstCount != secondCount)
                        return true;
                }

                return false;
            }

            private Dictionary<T, int> GetElementCounts(IEnumerable<T> enumerable, out int nullCount)
            {
                var dictionary = new Dictionary<T, int>(instanceComparer);
                nullCount = 0;

                foreach (T element in enumerable)
                {
                    if (element == null)
                    {
                        nullCount++;
                    }
                    else
                    {
                        int num;
                        dictionary.TryGetValue(element, out num);
                        num++;
                        dictionary[element] = num;
                    }
                }

                return dictionary;
            }

            public int GetHashCode(IEnumerable<T> enumerable)
            {
                var hash = 17;

                foreach (T val in enumerable.OrderBy(x => x))
                    hash = hash * 23 + instanceComparer.GetHashCode(val);

                return hash;
            }
        }

        /// <summary>
        /// Returns a read-only wrapper for the current list.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Collections.ObjectModel.ReadOnlyCollection`1"/> that acts as a read-only wrapper around the current list.
        /// </returns>
        public static ReadOnlyCollection<TValue> AsReadOnly<TValue>(this IList<TValue> list)
        {
            if (list == null)
                throw new ArgumentNullException(nameof(list));

            return new ReadOnlyCollection<TValue>(list);
        }

        /// <summary>
        /// Returns a read-only wrapper for the current dictionary.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Collections.ObjectModel.ReadOnlyDictionary`1"/> that acts as a read-only wrapper around the current dictionary.
        /// </returns>
        public static ReadOnlyDictionary<TKey, TValue> AsReadOnly<TKey, TValue>(this IDictionary<TKey, TValue> dictionary)
        {
            if (dictionary == null)
                throw new ArgumentNullException(nameof(dictionary));

            return new ReadOnlyDictionary<TKey, TValue>(dictionary);
        }

        public static IEnumerable<T> OfTypeOnly<T>(this IEnumerable enumerable)
        {
            if (enumerable == null)
                throw new ArgumentNullException(nameof(enumerable));

            return OfTypeOnlyInner<T>(enumerable);
        }

        private static IEnumerable<T> OfTypeOnlyInner<T>(IEnumerable enumerable)
        {
            // ReSharper disable once LoopCanBeConvertedToQuery <- do not want to create redundant iterators
            foreach (var obj in enumerable)
            {
                if (obj != null && obj.GetType() == typeof(T))
                    yield return (T)obj;
            }
        }

        /// <summary>
        /// Simple extension method to convert a single object to a an <see cref="IEnumerable{T}"/>
        /// </summary>
        /// <typeparam name="T">The generic type of the class that will be extended</typeparam>
        /// <param name="instance">The object to be converted to an <see cref="IEnumerable{T}"/></param>
        /// <returns>An <see cref="IEnumerable{T}"/> with one element</returns>
        /// <remarks>allows do easily avoid allocation of an array when only enumerator is required</remarks>
        public static IEnumerable<T> RepeatOnce<T>(this T instance)
        {
            yield return instance;
        }

        public static IAsyncEnumerable<T> RepeatOnceAsync<T>(this T instance)
        {
            return AsyncEnumerable.Repeat(instance, 1);
        }
    }
}