using OpenSmc.DataCubes;
using OpenSmc.Domain;

namespace OpenSmc.Hierarchies;

public interface IHierarchicalDimensionCache
{
    HierarchyNode<T> Get<T>(object id)
        where T : class, IHierarchicalDimension;

    IHierarchy<T> Get<T>()
        where T : class, IHierarchicalDimension;
}
