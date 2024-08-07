namespace MeshWeaver.Collections
{
    public interface IMemoryPool<T> 
        where T : IPoolable<T>
    {
        T Take();
        void Put(T instance);
    }

    public interface IPoolable<T> : IDisposable
        where T : IPoolable<T>
    {
        void SetPool(IMemoryPool<T> pool);
    }

    public class MemoryPool<T> : IMemoryPool<T> where T: IPoolable<T>
    {
        private readonly Func<T> factory;
        private readonly List<T> pool = new List<T>();

        public MemoryPool(Func<T> factory)
        {
            this.factory = factory;
        }

        public T Take()
        {
            lock (pool)
            {
                var index = pool.Count - 1;
                if (index >= 0)
                {
                    var pooled = pool[index];
                    pool.RemoveAt(index);
                    return pooled;
                }
            }
            var ret = factory();
            ret.SetPool(this);
            return ret;
        }

        public void Put(T instance)
        {
            lock (pool)
            {
                pool.Add(instance);
            }
        }
    }
}
