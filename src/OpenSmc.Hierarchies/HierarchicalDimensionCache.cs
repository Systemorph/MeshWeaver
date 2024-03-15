using OpenSmc.Data;
using OpenSmc.DataCubes;
using OpenSmc.Domain.Abstractions;
using OpenSmc.Reflection;

namespace OpenSmc.Hierarchies;

public class HierarchicalDimensionCache : IHierarchicalDimensionCache
{
    private readonly IWorkspace readOnlyWorkspace;
    private readonly Dictionary<Type, IHierarchy> cachedDimensions = new();

    public HierarchicalDimensionCache(IWorkspace readOnlyWorkspace)
    {
        this.readOnlyWorkspace = readOnlyWorkspace;
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

    public void Initialize(params DimensionDescriptor[] dimensionDescriptors)
    {
        foreach (var type in dimensionDescriptors.Where(d => d.Type != null).Select(d => d.Type))
        {
            if (typeof(IHierarchicalDimension).IsAssignableFrom(type))
                InitializeMethod.MakeGenericMethod(type).InvokeAsAction(this);
        }
    }

    private static readonly IGenericMethodCache InitializeMethod =
#pragma warning disable 4014
        GenericCaches.GetMethodCache<HierarchicalDimensionCache>(x => x.Initialize<IHierarchicalDimension>());
#pragma warning restore 4014

    public void Initialize<T>()
        where T : class, IHierarchicalDimension
    {
        if (readOnlyWorkspace != null && !cachedDimensions.TryGetValue(typeof(T), out _))
        {
            var hierarchy = new Hierarchy<T>(readOnlyWorkspace);
            hierarchy.Initialize();
            cachedDimensions[typeof(T)] = hierarchy;
        }
    }
}