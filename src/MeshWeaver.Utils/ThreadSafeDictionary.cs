#nullable enable
using System.Collections;
using System.Collections.Concurrent;

namespace MeshWeaver.Utils
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
        where TKey : notnull
    {
        private readonly IDictionary<TKey, TValue> innerDictionary;
        private readonly ConcurrentDictionary<TKey, TValue> innerDictionaryAsConcurrent;
        [NonSerialized]
        private readonly object factoryLockObject = new object();

        /// <summary>
        /// Initializes a new dictionary, optionally seeded from a collection and using a custom key comparer.
        /// </summary>
        /// <param name="collection">Key/value pairs to populate the dictionary with, or <c>null</c> for an empty dictionary.</param>
        /// <param name="comparer">Comparer used for key equality, or <c>null</c> for the default comparer.</param>
        public ThreadSafeDictionary(IEnumerable<KeyValuePair<TKey, TValue>>? collection = null,
                                    IEqualityComparer<TKey>? comparer = null)
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

        /// <summary>
        /// Returns the existing value for <paramref name="key"/>, or adds and returns <paramref name="value"/> if the key is absent.
        /// </summary>
        /// <param name="key">The key to look up or add.</param>
        /// <param name="value">The value to add when the key is absent.</param>
        /// <returns>The existing or newly added value.</returns>
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

        /// <summary>
        /// Adds <paramref name="addValue"/> if the key is absent, or replaces the existing value using <paramref name="updateValueFactory"/>.
        /// </summary>
        /// <param name="key">The key to add or update.</param>
        /// <param name="addValue">The value to add when the key is absent.</param>
        /// <param name="updateValueFactory">Function producing the new value from the key and its existing value.</param>
        /// <returns>The added or updated value.</returns>
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

        /// <summary>
        /// Adds the given key/value pair to the dictionary.
        /// </summary>
        /// <param name="item">The key/value pair to add.</param>
        public void Add(KeyValuePair<TKey, TValue> item)
        {
            innerDictionary.Add(item);
        }

        /// <summary>
        /// Adds the given key and value to the dictionary.
        /// </summary>
        /// <param name="key">The key to add.</param>
        /// <param name="value">The value to associate with the key.</param>
        public void Add(TKey key, TValue value)
        {
            innerDictionary.Add(key, value);
        }

        /// <summary>
        /// Copies the dictionary's key/value pairs into <paramref name="array"/> starting at <paramref name="arrayIndex"/>.
        /// </summary>
        /// <param name="array">The destination array.</param>
        /// <param name="arrayIndex">The zero-based index in the array at which copying begins.</param>
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            innerDictionary.CopyTo(array, arrayIndex);
        }

        /// <summary>
        /// Removes the entry identified by the key of <paramref name="item"/>.
        /// </summary>
        /// <param name="item">The key/value pair whose key identifies the entry to remove.</param>
        /// <returns><c>true</c> if an entry was removed; otherwise <c>false</c>.</returns>
        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            return innerDictionary.Remove(item.Key);
        }

        /// <summary>
        /// Removes the entry with the specified key.
        /// </summary>
        /// <param name="key">The key of the entry to remove.</param>
        /// <returns><c>true</c> if an entry was removed; otherwise <c>false</c>.</returns>
        public bool Remove(TKey key)
        {
            return innerDictionary.Remove(key);
        }

        /// <summary>
        /// Removes all entries from the dictionary.
        /// </summary>
        public void Clear()
        {
            innerDictionary.Clear();
        }

        /// <summary>
        /// Gets the number of entries in the dictionary.
        /// </summary>
        public int Count
        {
            get { return innerDictionary.Count; }
        }

        /// <summary>
        /// Gets a value indicating whether the dictionary is read-only. Always <c>false</c>.
        /// </summary>
        public bool IsReadOnly
        {
            get { return innerDictionary.IsReadOnly; }
        }

        /// <summary>
        /// Determines whether the dictionary contains the specified key.
        /// </summary>
        /// <param name="key">The key to locate.</param>
        /// <returns><c>true</c> if the key exists; otherwise <c>false</c>.</returns>
        public bool ContainsKey(TKey key)
        {
            return innerDictionary.ContainsKey(key);
        }

        /// <summary>
        /// Determines whether the dictionary contains the specified key/value pair.
        /// </summary>
        /// <param name="item">The key/value pair to locate.</param>
        /// <returns><c>true</c> if the pair exists; otherwise <c>false</c>.</returns>
        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            return innerDictionary.Contains(item);
        }

        /// <summary>
        /// Attempts to get the value associated with the specified key.
        /// </summary>
        /// <param name="key">The key to look up.</param>
        /// <param name="value">When this method returns, the value for the key if found; otherwise the default value.</param>
        /// <returns><c>true</c> if the key was found; otherwise <c>false</c>.</returns>
        public bool TryGetValue(TKey key, out TValue value)
        {
            return innerDictionary.TryGetValue(key, out value!);
        }

        /// <summary>
        /// Gets or sets the value associated with the specified key.
        /// </summary>
        /// <param name="key">The key whose value to get or set.</param>
        /// <returns>The value associated with the key.</returns>
        public TValue this[TKey key]
        {
            get { return innerDictionary[key]; }
            set { innerDictionary[key] = value; }
        }

        /// <summary>
        /// Gets a collection of all keys in the dictionary.
        /// </summary>
        public ICollection<TKey> Keys
        {
            get { return innerDictionary.Keys; }
        }

        /// <summary>
        /// Gets a collection of all values in the dictionary.
        /// </summary>
        public ICollection<TValue> Values
        {
            get { return innerDictionary.Values; }
        }

        /// <summary>
        /// Returns an enumerator over the dictionary's key/value pairs.
        /// </summary>
        /// <returns>An enumerator for the key/value pairs.</returns>
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
