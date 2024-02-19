using OpenSmc.Data;
using OpenSmc.DataCubes;
using OpenSmc.Domain.Abstractions;
using OpenSmc.Reflection;

namespace OpenSmc.Hierarchies;

public class HierarchicalDimensionCache : IHierarchicalDimensionCache
{
    private readonly IQuerySource querySource;
    private readonly Dictionary<Type, IHierarchy> cachedDimensions = new();

    public HierarchicalDimensionCache() {}

    public HierarchicalDimensionCache(IQuerySource querySource)
    {
        this.querySource = querySource;
    }

    public IHierarchyNode<T> Get<T>(string systemName)
        where T : class, IHierarchicalDimension
    {
        return Get<T>()?.GetHierarchyNode(systemName);
    }

    public IHierarchy<T> Get<T>()
        where T : class, IHierarchicalDimension
    {
        if (!cachedDimensions.TryGetValue(typeof(T), out var inner))
            return null;
        return (IHierarchy<T>)inner;
    }

    public async Task InitializeAsync(params DimensionDescriptor[] dimensionDescriptors)
    {
        foreach (var type in dimensionDescriptors.Where(d => d.Type != null).Select(d => d.Type))
        {
            if (typeof(IHierarchicalDimension).IsAssignableFrom(type))
                await InitializeAsyncMethod.MakeGenericMethod(type).InvokeAsActionAsync(this);
        }
    }

    private static readonly IGenericMethodCache InitializeAsyncMethod =
#pragma warning disable 4014
        GenericCaches.GetMethodCache<HierarchicalDimensionCache>(x => x.InitializeAsync<IHierarchicalDimension>());
#pragma warning restore 4014

    public async Task InitializeAsync<T>()
        where T : class, IHierarchicalDimension
    {
        if (querySource != null && !cachedDimensions.TryGetValue(typeof(T), out _))
        {
            var hierarchy = new Hierarchy<T>(querySource);
            await hierarchy.InitializeAsync();
            cachedDimensions[typeof(T)] = hierarchy;
        }
    }

    public void Initialize<T>(IDictionary<string, T> outerElementsBySystemName)
        where T : class, IHierarchicalDimension
    {
        if (outerElementsBySystemName != null && !cachedDimensions.TryGetValue(typeof(T), out _))
        {
            var hierarchy = new Hierarchy<T>(outerElementsBySystemName);
            hierarchy.Initialize();
            cachedDimensions[typeof(T)] = hierarchy;
        }
    }
}