using OpenSmc.DataCubes;
using OpenSmc.Domain.Abstractions;

namespace OpenSmc.Hierarchies;

public interface IHierarchicalDimensionCache
{
    IHierarchyNode<T> Get<T>(string systemName)
        where T : class, IHierarchicalDimension;
    
    IHierarchy<T> Get<T>()
        where T : class, IHierarchicalDimension;

    Task InitializeAsync<T>()
        where T : class, IHierarchicalDimension;

    void Initialize<T>(IDictionary<string, T> outerElementsBySystemName)
        where T : class, IHierarchicalDimension;    

    Task InitializeAsync(params DimensionDescriptor[] dimensionDescriptors);
}