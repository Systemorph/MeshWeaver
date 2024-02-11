using OpenSmc.Data;
using OpenSmc.DataCubes;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.Domain.Abstractions;
using OpenSmc.Reflection;

namespace OpenSmc.Pivot
{
    public interface IDimensionCache
    {
        T Get<T>(string systemName)
            where T : class, INamed;

        IEnumerable<T> GetAll<T>()
            where T : class, INamed;

        void Initialize(IEnumerable<DimensionDescriptor> dimensionDescriptors);
    }

    public class DimensionCache : IDimensionCache
    {
        private readonly IQuerySource querySource;
        private readonly Dictionary<Type, IDimensionTypeCache> cachedDimensions = new();
        private Dictionary<string, DimensionDescriptor> knownDimensions = new();

        public DimensionCache(IQuerySource querySource)
        {
            this.querySource = querySource;
        }

        public T Get<T>(string systemName)
            where T : class, INamed
        {
            if (!cachedDimensions.TryGetValue(typeof(T), out var inner))
                return null;
            return ((DimensionTypeCache<T>)inner).Get(systemName);
        }

        public IEnumerable<T> GetAll<T>()
            where T : class, INamed
        {
            if (!cachedDimensions.TryGetValue(typeof(T), out var inner))
                return null;
            return ((DimensionTypeCache<T>)inner).GetAll();
        }


        public void Initialize(IEnumerable<DimensionDescriptor> dimensionDescriptors)
        {
            knownDimensions = dimensionDescriptors.ToDictionary(x => x.SystemName);

            foreach (var type in knownDimensions.Values.Where(d => d.Type != null).Select(d => d.Type))
            {
                if (typeof(INamed).IsAssignableFrom(type))
                    InitializeMethod.MakeGenericMethod(type).InvokeAsAction(this);
            }
        }

        private static readonly IGenericMethodCache InitializeMethod =
#pragma warning disable 4014
            GenericCaches.GetMethodCache<DimensionCache>(x => x.Initialize<INamed>());
#pragma warning restore 4014

        private void Initialize<T>()
            where T : class, INamed
        {
            var query = querySource.Query<T>();
            if (query != null)
            {
                cachedDimensions[typeof(T)] = new DimensionTypeCache<T>(query.ToDictionary(x => x.SystemName));
            }
        }

        private interface IDimensionTypeCache
        {
            INamed Get(string systemName);
        }

        private class DimensionTypeCache<T> : IDimensionTypeCache
            where T : INamed
        {
            private readonly IDictionary<string, T> elementsBySystemName;

            public DimensionTypeCache(IDictionary<string, T> elements)
            {
                elementsBySystemName = elements;
            }

            public T Get(string systemName)
            {
                elementsBySystemName.TryGetValue(systemName, out var ret);
                return ret;
            }

            public IEnumerable<T> GetAll()
            {
                return elementsBySystemName.Values;
            }

            INamed IDimensionTypeCache.Get(string systemName)
            {
                return Get(systemName);
            }
        }
    }
}