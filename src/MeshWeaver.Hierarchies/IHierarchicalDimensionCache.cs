using MeshWeaver.DataCubes;
using MeshWeaver.Domain;

namespace MeshWeaver.Hierarchies;

public interface IHierarchicalDimensionCache
{
    bool Has(Type type);
    HierarchyNode<T> Get<T>(object id)
        where T : class, IHierarchicalDimension;

    IHierarchy<T> Get<T>()
        where T : class, IHierarchicalDimension;
}
