using MeshWeaver.Domain;

namespace MeshWeaver.Hierarchies;

public static class HierarchicalDimensionExtensions
{
    public static T Parent<T>(this IHierarchy<T> cache, object id)
        where T : class, IHierarchicalDimension
    {
        return cache.GetHierarchyNode(id).Parent;
    }

    public static T AncestorAtLevel<T>(this DimensionCache cache, object id, int level)
        where T : class, IHierarchicalDimension
    {
        var hierarchy = cache.GetHierarchical<T>();
        var ret = hierarchy.GetHierarchyNode(id);
        if (ret.Level < level)
            return null;
        while (ret.Level > level)
            ret = hierarchy.GetHierarchyNode(ret.ParentId);
        return ret.Element;

    }

    public static object AncestorIdAtLevel<T>(
        this DimensionCache cache,
        object id,
        int level
    )
        where T : class, IHierarchicalDimension
    {
        var hierarchy = cache.GetHierarchical<T>();
        var ret = hierarchy.GetHierarchyNode(id);
        if (ret == null)
            return id;
        if (ret.Level < level)
            return null;
        while (ret.Level > level)
            ret = hierarchy.GetHierarchyNode(ret.ParentId);
        return ret.Id;
    }

}
