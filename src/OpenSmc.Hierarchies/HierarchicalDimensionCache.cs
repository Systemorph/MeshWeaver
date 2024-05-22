using OpenSmc.Data;
using OpenSmc.Domain;

namespace OpenSmc.Hierarchies;

public class HierarchicalDimensionCache(WorkspaceState state) : IHierarchicalDimensionCache
{
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
