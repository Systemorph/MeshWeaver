using System.Collections.Concurrent;

namespace OpenSmc.Collections
{
    /// <summary>
    /// Represents a thread-safe collection of keys and values.
    /// </summary>
    /// <typeparam name="TKey">The type of the keys in the dictionary.</typeparam>
    /// <typeparam name="TValue">The type of the values in the dictionary.</typeparam>
    /// <remarks>
    /// All public and protected members of <see cref="ThreadSafeDictionary{TKey,TValue}"/> are thread-safe and may be used
    /// concurrently from multiple threads.
    /// </remarks>
    [Serializable]
    public class ThreadSafeDictionary<TKey, TValue> : IDictionary<TKey, TValue>
    {
        private readonly IDictionary<TKey, TValue> innerDictionary;
        private readonly ConcurrentDictionary<TKey, TValue> innerDictionaryAsConcurrent;
        [NonSerialized]
        private readonly object factoryLockObject = new object();

        public ThreadSafeDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection = null,
                                    IEqualityComparer<TKey> comparer = null)
        {
            ConcurrentDictionary<TKey, TValue> dictionary;
            if (collection == null)
            {
                dictionary = comparer == null
                                 ? new ConcurrentDictionary<TKey, TValue>()
                                 : new ConcurrentDictionary<TKey, TValue>(comparer);
            }
            else
            {
                dictionary = comparer == null
                                 ? new ConcurrentDictionary<TKey, TValue>(collection)
                                 : new ConcurrentDictionary<TKey, TValue>(collection, comparer);
            }
            
            innerDictionary = innerDictionaryAsConcurrent = dictionary;
        }

        public TValue GetOrAdd(TKey key, TValue value)
        {
            // ReSharper disable once InconsistentlySynchronizedField
            return innerDictionaryAsConcurrent.GetOrAdd(key, value);
        }

        /// <summary>
        /// Adds a key/value pair to the <see cref="ThreadSafeDictionary{TKey,TValue}"/> 
        /// if the key does not already exist.
        /// </summary>
        /// <param name="key">The key of the element to add.</param>
        /// <param name="valueFactory">The function used to generate a value for the key</param>
        /// <returns>The value for the key.  This will be either the existing value for the key if the
        /// key is already in the dictionary, or the new value for the key as returned by valueFactory
        /// if the key was not in the dictionary.</returns>
        /// <remarks>Even if you call GetOrAdd simultaneously on different threads, valueFactory will not be called multiple times.</remarks>
        public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            lock (factoryLockObject)
                return innerDictionaryAsConcurrent.GetOrAdd(key, valueFactory);
        }

        public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updateValueFactory)
        {
            // ReSharper disable once InconsistentlySynchronizedField
            return innerDictionaryAsConcurrent.AddOrUpdate(key, addValue, updateValueFactory);
        }

        /// <summary>
        /// Adds a key/value pair to the <see cref="ThreadSafeDictionary{TKey,TValue}"/> if the key does not already 
        /// exist, or updates a key/value pair in the <see cref="ThreadSafeDictionary{TKey,TValue}"/> if the key 
        /// already exists.
        /// </summary>
        /// <param name="key">The key to be added or whose value should be updated</param>
        /// <param name="addValueFactory">The function used to generate a value for an absent key</param>
        /// <param name="updateValueFactory">The function used to generate a new value for an existing key
        /// based on the key's existing value</param>
        /// <returns>The new value for the key. This will be either be the result of addValueFactory (if the key was absent) or the result of updateValueFactory (if the key was present).</returns>
        /// <remarks>Even if you call AddOrUpdate simultaneously on different threads, addValueFactory will not be called multiple times.</remarks>
        public TValue AddOrUpdate(TKey key, Func<TKey, TValue> addValueFactory, Func<TKey, TValue, TValue> updateValueFactory)
        {
            lock (factoryLockObject)
                return innerDictionaryAsConcurrent.AddOrUpdate(key, addValueFactory, updateValueFactory);
        }

        public void Add(KeyValuePair<TKey, TValue> item)
        {
            innerDictionary.Add(item);
        }

        public void Add(TKey key, TValue value)
        {
            innerDictionary.Add(key, value);
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            innerDictionary.CopyTo(array, arrayIndex);
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            return innerDictionary.Remove(item.Key);
        }

        public bool Remove(TKey key)
        {
            return innerDictionary.Remove(key);
        }

        public void Clear()
        {
            innerDictionary.Clear();
        }

        public int Count
        {
            get { return innerDictionary.Count; }
        }

        public bool IsReadOnly
        {
            get { return innerDictionary.IsReadOnly; }
        }

        public bool ContainsKey(TKey key)
        {
            return innerDictionary.ContainsKey(key);
        }

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            return innerDictionary.Contains(item);
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            return innerDictionary.TryGetValue(key, out value);
        }

        public TValue this[TKey key]
        {
            get { return innerDictionary[key]; }
            set { innerDictionary[key] = value; }
        }

        public ICollection<TKey> Keys
        {
            get { return innerDictionary.Keys; }
        }

        public ICollection<TValue> Values
        {
            get { return innerDictionary.Values; }
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return innerDictionary.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}