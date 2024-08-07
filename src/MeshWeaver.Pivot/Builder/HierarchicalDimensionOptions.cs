using MeshWeaver.Domain;

namespace MeshWeaver.Pivot.Builder;

public class HierarchicalDimensionOptions : IHierarchicalDimensionOptions
{
    private readonly Dictionary<Type, int> levelMax = new();
    private readonly Dictionary<Type, int> levelMin = new();
    private readonly HashSet<Type> flatHierarchies = new();

    public IHierarchicalDimensionOptions LevelMax<T>(int level)
        where T : class, IHierarchicalDimension
    {
        levelMax[typeof(T)] = level;
        return this;
    }

    public int GetLevelMax<T>()
        where T : class, IHierarchicalDimension
    {
        if (!levelMax.TryGetValue(typeof(T), out var level))
            return int.MaxValue;
        return level;
    }

    public IHierarchicalDimensionOptions LevelMin<T>(int level)
        where T : class, IHierarchicalDimension
    {
        levelMin[typeof(T)] = level;
        return this;
    }

    public int GetLevelMin<T>()
        where T : class, IHierarchicalDimension
    {
        if (!levelMin.TryGetValue(typeof(T), out var level))
            return 0;
        return level;
    }

    public IHierarchicalDimensionOptions Flatten<T>()
        where T : class, IHierarchicalDimension
    {
        flatHierarchies.Add(typeof(T));
        return this;
    }

    public bool IsFlat<T>()
        where T : class, IHierarchicalDimension
    {
        return flatHierarchies.Contains(typeof(T));
    }
}
