#nullable enable
/******************************************************************************************************
 * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
 ******************************************************************************************************/

namespace MeshWeaver.Utils
{
    /// <summary>
    /// Base class for a thread-safe, lazily-populated store that creates a single value instance per key on demand
    /// and caches it. Reads are lock-free against an immutable snapshot; writes copy-on-write under a lock.
    /// </summary>
    /// <typeparam name="TKey">The key type. Must be non-nullable.</typeparam>
    /// <typeparam name="TValue">The type of the values created and cached per key.</typeparam>
    public abstract class CreatableObjectStoreBase<TKey, TValue>
        where TKey : notnull
    {
        private readonly object nullLockObject = new object();
        private volatile bool nullMainValueInitialized;
        private TValue nullValue = default!;

        private volatile Dictionary<TKey, TValue> cache = null!;
        private readonly object locker = new object();
        /// <summary>
        /// Initializes the store with an optional key comparer and a set of pre-populated key/value pairs.
        /// </summary>
        /// <param name="keyComparer">Comparer used for key lookups, or <c>null</c> for the default comparer.</param>
        /// <param name="initialValues">Key/value pairs to seed the cache with.</param>
        protected CreatableObjectStoreBase(IEqualityComparer<TKey>? keyComparer, KeyValuePair<TKey, TValue>[] initialValues)
        {
            cache = keyComparer != null
                ? new Dictionary<TKey, TValue>(keyComparer)
                : new Dictionary<TKey, TValue>();

            foreach (var initialValue in initialValues)
            {
                cache.Add(initialValue.Key, initialValue.Value);
            }
        }

        /// <summary>
        /// Returns the cached value for <paramref name="key"/>, creating it via <see cref="Create"/> on first access.
        /// </summary>
        /// <param name="key">The key whose value to retrieve or create.</param>
        /// <returns>The existing or newly created value for the key.</returns>
        public TValue GetInstance(TKey key)
        {
            return GetInstance(key, Create);
        }

        /// <summary>
        /// Returns the cached value for <paramref name="key"/>, creating it via <paramref name="valueFactory"/> on first access.
        /// </summary>
        /// <param name="key">The key whose value to retrieve or create.</param>
        /// <param name="valueFactory">Factory invoked to create the value when the key is not yet cached.</param>
        /// <returns>The existing or newly created value for the key.</returns>
        protected TValue GetInstance(TKey key, Func<TKey, TValue> valueFactory)
        {
            if (valueFactory == null)
                throw new ArgumentNullException(nameof(valueFactory));

            if (key == null)
                return GetOrCreateNullKeyInstance(default!, valueFactory);

            if (cache.TryGetValue(key, out var ret))
                return ret;

            lock (locker)
            {
                if (cache.TryGetValue(key, out ret))
                    return ret;

                var newDict = new Dictionary<TKey, TValue>(cache, cache.Comparer) { { key, ret = valueFactory(key) } };
                cache = newDict;
                return ret;
            }
        }

        private TValue GetOrCreateNullKeyInstance(TKey key, Func<TKey, TValue> valueFactory)
        {
            if (nullMainValueInitialized)
                return nullValue;

            lock (nullLockObject)
            {
                if (nullMainValueInitialized)
                    return nullValue;

                nullValue = valueFactory(key);
                nullMainValueInitialized = true;
                return nullValue;
            }
        }

        /// <summary>
        /// Creates the value for a key that is not yet cached.
        /// </summary>
        /// <param name="key">The key to create a value for.</param>
        /// <returns>The newly created value.</returns>
        protected abstract TValue Create(TKey key);
    }

    /// <summary>
    /// A <see cref="CreatableObjectStoreBase{TKey,TValue}"/> that creates missing values from a default factory supplied at construction.
    /// </summary>
    /// <typeparam name="TKey">The key type. Must be non-nullable.</typeparam>
    /// <typeparam name="TValue">The type of the values created and cached per key.</typeparam>
    public class CreatableObjectStore<TKey, TValue> : CreatableObjectStoreBase<TKey, TValue>
        where TKey : notnull
    {
        private readonly Func<TKey, TValue>? defaultValueFactory;

        /// <summary>
        /// Initializes the store with an optional default value factory, key comparer and seed values.
        /// </summary>
        /// <param name="defaultValueFactory">Factory used to create values when no per-call factory is provided; may be <c>null</c>.</param>
        /// <param name="keyComparer">Comparer used for key lookups, or <c>null</c> for the default comparer.</param>
        /// <param name="initialValues">Key/value pairs to seed the cache with.</param>
        public CreatableObjectStore(Func<TKey, TValue>? defaultValueFactory = null, IEqualityComparer<TKey>? keyComparer = null, params KeyValuePair<TKey, TValue>[] initialValues)
            : base(keyComparer, initialValues)
        {
            this.defaultValueFactory = defaultValueFactory;
        }

        /// <summary>
        /// Returns the cached value for <paramref name="key"/>, creating it via <paramref name="factory"/> (or the default factory) on first access.
        /// </summary>
        /// <param name="key">The key whose value to retrieve or create.</param>
        /// <param name="factory">Factory to create the value; falls back to the default factory when <c>null</c>.</param>
        /// <returns>The existing or newly created value for the key.</returns>
        public new TValue GetInstance(TKey key, Func<TKey, TValue>? factory = null)
        {
            return base.GetInstance(key, factory ?? defaultValueFactory!);
        }

        /// <summary>
        /// Creates the value for a key using the default value factory.
        /// </summary>
        /// <param name="key">The key to create a value for.</param>
        /// <returns>The newly created value.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no default value factory was supplied.</exception>
        protected override TValue Create(TKey key)
        {
            if (defaultValueFactory == null)
                throw new InvalidOperationException("Default value factory is missing");

            return defaultValueFactory(key);
        }
    }

    /// <summary>
    /// A two-level <see cref="CreatableObjectStore{TKey,TValue}"/> keyed by a pair of keys, where each first-level key maps to a nested store keyed by the second key.
    /// </summary>
    /// <typeparam name="TKey1">The first-level key type. Must be non-nullable.</typeparam>
    /// <typeparam name="TKey2">The second-level key type. Must be non-nullable.</typeparam>
    /// <typeparam name="TValue">The type of the values cached per key pair.</typeparam>
    public class CreatableObjectStore<TKey1, TKey2, TValue>
        : CreatableObjectStore<TKey1, CreatableObjectStore<TKey2, TValue>>
        where TKey1 : notnull
        where TKey2 : notnull
    {
        /// <summary>
        /// Initializes the store with a factory that produces a value from the full key pair.
        /// </summary>
        /// <param name="defaultFactory">Factory invoked with both keys to create a value.</param>
        public CreatableObjectStore(Func<TKey1, TKey2, TValue> defaultFactory)
            : base(x => new CreatableObjectStore<TKey2, TValue>(y => defaultFactory(x, y)))
        { }

        /// <summary>
        /// Initializes the store with an optional factory that produces the nested second-level store for a first-level key.
        /// </summary>
        /// <param name="defaultValueFactory">Factory producing the nested store for a first-level key; may be <c>null</c>.</param>
        /// <param name="key1Comparer">Comparer for the first-level key, or <c>null</c> for the default comparer.</param>
        public CreatableObjectStore(Func<TKey1, CreatableObjectStore<TKey2, TValue>>? defaultValueFactory = null, IEqualityComparer<TKey1>? key1Comparer = null)
            : base(defaultValueFactory, key1Comparer)
        {
        }

        /// <summary>
        /// Returns the cached value for the key pair, creating intermediate and leaf entries on first access.
        /// </summary>
        /// <param name="key1">The first-level key.</param>
        /// <param name="key2">The second-level key.</param>
        /// <param name="factory1">Optional factory producing the nested second-level store for <paramref name="key1"/>.</param>
        /// <param name="factory2">Optional factory producing the value for <paramref name="key2"/>.</param>
        /// <returns>The existing or newly created value for the key pair.</returns>
        public TValue GetInstance(TKey1 key1, TKey2 key2,
                                  Func<TKey1, CreatableObjectStore<TKey2, TValue>>? factory1 = null,
                                  Func<TKey2, TValue>? factory2 = null)
        {
            var store = GetInstance(key1, factory1);
            return store.GetInstance(key2, factory2);
        }
    }

    /// <summary>
    /// A three-level <see cref="CreatableObjectStore{TKey,TValue}"/> keyed by a triple of keys, with nested stores at each level.
    /// </summary>
    /// <typeparam name="TKey1">The first-level key type. Must be non-nullable.</typeparam>
    /// <typeparam name="TKey2">The second-level key type. Must be non-nullable.</typeparam>
    /// <typeparam name="TKey3">The third-level key type. Must be non-nullable.</typeparam>
    /// <typeparam name="TValue">The type of the values cached per key triple.</typeparam>
    public class CreatableObjectStore<TKey1, TKey2, TKey3, TValue>
        : CreatableObjectStore<TKey1, TKey2, CreatableObjectStore<TKey3, TValue>>
        where TKey1 : notnull
        where TKey2 : notnull
        where TKey3 : notnull
    {
        /// <summary>
        /// Initializes the store with an optional factory producing the nested two-level store for a first-level key.
        /// </summary>
        /// <param name="defaultValueFactory">Factory producing the nested store for a first-level key; may be <c>null</c>.</param>
        /// <param name="key1Comparer">Comparer for the first-level key, or <c>null</c> for the default comparer.</param>
        public CreatableObjectStore(Func<TKey1, CreatableObjectStore<TKey2, TKey3, TValue>>? defaultValueFactory = null,
                                    IEqualityComparer<TKey1>? key1Comparer = null)
            : base(defaultValueFactory, key1Comparer)
        {
        }
        /// <summary>
        /// Initializes the store with a factory that produces a value from the full key triple.
        /// </summary>
        /// <param name="defaultValueFactory">Factory invoked with all three keys to create a value.</param>
        /// <param name="key1Comparer">Comparer for the first-level key, or <c>null</c> for the default comparer.</param>
        public CreatableObjectStore(Func<TKey1, TKey2, TKey3, TValue> defaultValueFactory,
                                   IEqualityComparer<TKey1>? key1Comparer = null)
            : base(x => new CreatableObjectStore<TKey2, CreatableObjectStore<TKey3, TValue>>((y) => new CreatableObjectStore<TKey3, TValue>(z => defaultValueFactory(x, y, z))), key1Comparer)
        {
        }

        /// <summary>
        /// Returns the cached value for the key triple, creating intermediate and leaf entries on first access.
        /// </summary>
        /// <param name="key1">The first-level key.</param>
        /// <param name="key2">The second-level key.</param>
        /// <param name="key3">The third-level key.</param>
        /// <param name="factory1">Optional factory producing the nested two-level store for <paramref name="key1"/>.</param>
        /// <param name="factory2">Optional factory producing the nested store for <paramref name="key2"/>.</param>
        /// <param name="factory3">Optional factory producing the value for <paramref name="key3"/>.</param>
        /// <returns>The existing or newly created value for the key triple.</returns>
        public TValue GetInstance(TKey1 key1, TKey2 key2, TKey3 key3,
                                  Func<TKey1, CreatableObjectStore<TKey2, CreatableObjectStore<TKey3, TValue>>>? factory1 = null,
                                  Func<TKey2, CreatableObjectStore<TKey3, TValue>>? factory2 = null,
                                  Func<TKey3, TValue>? factory3 = null)
        {
            var store = GetInstance(key1, key2, factory1, factory2);
            return store.GetInstance(key3, factory3);
        }
    }
}
