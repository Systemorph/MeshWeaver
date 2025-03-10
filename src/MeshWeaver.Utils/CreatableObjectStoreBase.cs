/******************************************************************************************************
 * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
 ******************************************************************************************************/

namespace MeshWeaver.Utils
{
    public abstract class CreatableObjectStoreBase<TKey, TValue>  
    {
        private readonly object nullLockObject = new object();
        private volatile bool nullMainValueInitialized;
        private TValue nullValue;

        private volatile Dictionary<TKey, TValue> cache;
        private readonly object locker = new object();
        protected CreatableObjectStoreBase(IEqualityComparer<TKey> keyComparer, KeyValuePair<TKey, TValue>[] initialValues)
        {
            cache = keyComparer != null
                ? new Dictionary<TKey, TValue>(keyComparer)
                : new Dictionary<TKey, TValue>();

            foreach (var initialValue in initialValues)
            {
                cache.Add(initialValue.Key,initialValue.Value);
            }
        }

        public TValue GetInstance(TKey key)
        {
            return GetInstance(key, Create);
        }

        protected TValue GetInstance(TKey key, Func<TKey, TValue> valueFactory)
        {
            if (valueFactory == null)
                throw new ArgumentNullException(nameof(valueFactory));

            if (key == null)
                return GetOrCreateNullKeyInstance(default, valueFactory);

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

        protected abstract TValue Create(TKey key);
    }

    public class CreatableObjectStore<TKey, TValue> : CreatableObjectStoreBase<TKey, TValue> 
    {
        private readonly Func<TKey,TValue> defaultValueFactory;

        public CreatableObjectStore(Func<TKey, TValue> defaultValueFactory = null, IEqualityComparer<TKey> keyComparer = null, params KeyValuePair<TKey,TValue>[] initialValues)
            : base(keyComparer, initialValues)
        {
            this.defaultValueFactory = defaultValueFactory;
        }

        public new TValue GetInstance(TKey key, Func<TKey, TValue> factory = null)
        {
            return base.GetInstance(key, factory ?? defaultValueFactory);
        }

        protected override TValue Create(TKey key)
        {
            if (defaultValueFactory == null)
                throw new InvalidOperationException("Default value factory is missing");

            return defaultValueFactory(key);
        }
    }

    public class CreatableObjectStore<TKey1, TKey2, TValue> 
        : CreatableObjectStore<TKey1, CreatableObjectStore<TKey2, TValue>>
    {
        public CreatableObjectStore(Func<TKey1,TKey2,TValue> defaultFactory)
            : base(x => new CreatableObjectStore<TKey2, TValue>(y => defaultFactory(x,y)))
        { }

        public CreatableObjectStore(Func<TKey1, CreatableObjectStore<TKey2, TValue>> defaultValueFactory = null, IEqualityComparer<TKey1> key1Comparer = null) 
            : base(defaultValueFactory, key1Comparer)
        {
        }

        public TValue GetInstance(TKey1 key1, TKey2 key2,
                                  Func<TKey1, CreatableObjectStore<TKey2, TValue>> factory1 = null,
                                  Func<TKey2, TValue> factory2 = null)
        {
            var store = GetInstance(key1, factory1);
            return store.GetInstance(key2, factory2);
        }
    }

    public class CreatableObjectStore<TKey1, TKey2, TKey3, TValue>
        : CreatableObjectStore<TKey1, TKey2, CreatableObjectStore<TKey3, TValue>>
    {
        public CreatableObjectStore(Func<TKey1, CreatableObjectStore<TKey2, TKey3, TValue>> defaultValueFactory = null,
                                    IEqualityComparer<TKey1> key1Comparer = null)
            : base(defaultValueFactory, key1Comparer)
        {
        }
        public CreatableObjectStore(Func<TKey1,TKey2, TKey3, TValue> defaultValueFactory,
                                   IEqualityComparer<TKey1> key1Comparer = null)
            : base(x => new CreatableObjectStore<TKey2, CreatableObjectStore<TKey3, TValue>>((y) => new CreatableObjectStore<TKey3, TValue>(z => defaultValueFactory(x, y, z))), key1Comparer)
        {
        }

        public TValue GetInstance(TKey1 key1, TKey2 key2, TKey3 key3,
                                  Func<TKey1, CreatableObjectStore<TKey2, CreatableObjectStore<TKey3, TValue>>> factory1 = null,
                                  Func<TKey2, CreatableObjectStore<TKey3, TValue>> factory2 = null,
                                  Func<TKey3, TValue> factory3 = null)
        {
            var store = GetInstance(key1, key2, factory1, factory2);
            return store.GetInstance(key3, factory3);
        }
    }
}
