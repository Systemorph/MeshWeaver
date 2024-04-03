using OpenSmc.DataCubes;
using OpenSmc.Domain.Abstractions;

namespace OpenSmc.Hierarchies;

public interface IHierarchicalDimensionCache
{
    IHierarchyNode<T> Get<T>(string systemName)
        where T : class, IHierarchicalDimension;
    
    IHierarchy<T> Get<T>()
        where T : class, IHierarchicalDimension;

    void Initialize<T>()
        where T : class, IHierarchicalDimension;

    void Initialize(params DimensionDescriptor[] dimensionDescriptors);
}