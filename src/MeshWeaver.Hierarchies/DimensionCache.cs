using MeshWeaver.Data;
using MeshWeaver.Domain;

namespace MeshWeaver.Hierarchies;

public class DimensionCache(IWorkspace workspace, EntityStore store)
{
    public bool Has(Type type)
    {
        return workspace.DataContext.MappedTypes.Contains(type);
    }



    public T Get<T>(object id)
        where T : class =>
        (T)Get(typeof(T))?.GetValueOrDefault(id);

    public IReadOnlyDictionary<object,object> Get(Type type)
    {
        var collection = workspace.DataContext.GetCollectionName(type);
        return collection == null ? null : store.GetCollection(collection)?.Instances;
    }

    public HierarchyNode<T> GetHierarchical<T>(object id)
        where T : class, IHierarchicalDimension
    {
        return GetHierarchical<T>()?.GetHierarchyNode(id);
    }

    public IHierarchy<T> GetHierarchical<T>()
        where T : class, IHierarchicalDimension
    {
        return new Hierarchy<T>(Get(typeof(T)));
    }

    public T Parent<T>(string dim) where T : class, IHierarchicalDimension => GetHierarchical<T>(dim)?.Parent;
}
