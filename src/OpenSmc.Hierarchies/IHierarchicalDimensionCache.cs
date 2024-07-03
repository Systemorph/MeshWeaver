using OpenSmc.DataCubes;
using OpenSmc.Domain;

namespace OpenSmc.Hierarchies;

public interface IHierarchicalDimensionCache
{
    bool Has(Type type);
    HierarchyNode<T> Get<T>(object id)
        where T : class, IHierarchicalDimension;

    IHierarchy<T> Get<T>()
        where T : class, IHierarchicalDimension;
}
