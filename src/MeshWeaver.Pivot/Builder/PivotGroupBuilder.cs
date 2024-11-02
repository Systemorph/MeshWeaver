using MeshWeaver.DataCubes;
using MeshWeaver.Hierarchies;
using MeshWeaver.Pivot.Grouping;
using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Builder;

public class PivotGroupBuilder<T, TGroup>(
    Func<DimensionCache, IPivotGrouper<T, TGroup>> factory) where TGroup : class, IGroup, new()
{
    public IPivotGrouper<T, TGroup> GetGrouper(DimensionCache dimensionCache)
        => factory.Invoke(dimensionCache);
}
