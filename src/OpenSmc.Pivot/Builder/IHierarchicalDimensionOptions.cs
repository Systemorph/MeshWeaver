using OpenSmc.Domain.Abstractions;

namespace OpenSmc.Pivot.Builder;

public interface IHierarchicalDimensionOptions
{
    IHierarchicalDimensionOptions LevelMax<T>(int level)
        where T : class, IHierarchicalDimension;
    int GetLevelMax<T>()
        where T : class, IHierarchicalDimension;
    IHierarchicalDimensionOptions LevelMin<T>(int level)
        where T : class, IHierarchicalDimension;
    int GetLevelMin<T>()
        where T : class, IHierarchicalDimension;
    IHierarchicalDimensionOptions Flatten<T>()
        where T : class, IHierarchicalDimension;
    bool IsFlat<T>()
        where T : class, IHierarchicalDimension;
}