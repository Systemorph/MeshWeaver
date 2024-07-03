using OpenSmc.Data;
using OpenSmc.Domain;

namespace OpenSmc.Hierarchies;

public static class HierarchicalDimensionExtensions
{
    public static IHierarchicalDimensionCache ToHierarchicalDimensionCache(
        this WorkspaceState state
    )
    {
        return new HierarchicalDimensionCache(state);
    }

    public static T Parent<T>(this IHierarchicalDimensionCache cache, object id)
        where T : class, IHierarchicalDimension
    {
        return cache.Get<T>(id).Parent;
    }

    public static T AncestorAtLevel<T>(this IHierarchicalDimensionCache cache, object id, int level)
        where T : class, IHierarchicalDimension
    {
        var ret = cache.Get<T>(id);
        if (ret.Level < level)
            return null;
        while (ret.Level > level)
            ret = cache.Get<T>(ret.ParentId);
        return ret.Element;
    }

    public static object AncestorIdAtLevel<T>(
        this IHierarchicalDimensionCache cache,
        object id,
        int level
    )
        where T : class, IHierarchicalDimension
    {
        var ret = cache.Get<T>(id);
        if (ret == null)
            return id;
        if (ret.Level < level)
            return null;
        while (ret.Level > level)
            ret = cache.Get<T>(ret.ParentId);
        return ret.Id;
    }
}
