using OpenSmc.Data;

namespace OpenSmc.Hierarchies;

public static class HierarchicalDimensionExtensions
{
    public static IHierarchicalDimensionCache ToHierarchicalDimensionCache(this IReadOnlyWorkspace readOnlyWorkspace)
    {
        return new HierarchicalDimensionCache(readOnlyWorkspace);
    }
}