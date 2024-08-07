using MeshWeaver.Data;
using MeshWeaver.Domain;

namespace MeshWeaver.Hierarchies;

public class HierarchicalDimensionCache(WorkspaceState state) : IHierarchicalDimensionCache
{
    public bool Has(Type type)
    {
        return state.Has(type);
    }

    public HierarchyNode<T> Get<T>(object id)
        where T : class, IHierarchicalDimension
    {
        return Get<T>()?.GetHierarchyNode(id);
    }

    public IHierarchy<T> Get<T>()
        where T : class, IHierarchicalDimension
    {
        return new Hierarchy<T>(state.GetDataById<T>());
    }
}
