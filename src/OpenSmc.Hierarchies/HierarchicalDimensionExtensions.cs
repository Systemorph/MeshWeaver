using OpenSmc.Data;

namespace OpenSmc.Hierarchies;

public static class HierarchicalDimensionExtensions
{
    public static IHierarchicalDimensionCache ToHierarchicalDimensionCache(this IWorkspace readOnlyWorkspace)
    {
        return new HierarchicalDimensionCache(readOnlyWorkspace);
    }
}