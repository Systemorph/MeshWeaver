using OpenSmc.DataSource.Abstractions;

namespace OpenSmc.Hierarchies;

public static class HierarchicalDimensionExtensions
{
    public static IHierarchicalDimensionCache ToHierarchicalDimensionCache(this IQuerySource querySource)
    {
        return new HierarchicalDimensionCache(querySource);
    }
}