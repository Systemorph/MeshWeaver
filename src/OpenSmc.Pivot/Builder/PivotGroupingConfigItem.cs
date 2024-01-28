using System.Diagnostics.CodeAnalysis;
using OpenSmc.Pivot.Aggregations;
using OpenSmc.Pivot.Grouping;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Builder
{
    public record PivotGroupingConfigItem<T, TGroup>
        where TGroup : class, IGroup, new()
    {
        public PivotGroupingConfigItem([NotNull] IPivotGrouper<T, TGroup> grouping)
        {
            Grouping = grouping;
        }

        internal IPivotGrouper<T, TGroup> Grouping { get; init; }


        public virtual PivotGroupManager<T, TIntermediate, TAggregate, TGroup> GetGroupManager<TIntermediate, TAggregate>(PivotGroupManager<T, TIntermediate, TAggregate, TGroup> subGroup,
                                                                                                                          Aggregations<T, TIntermediate, TAggregate> aggregationFunctions, IDimensionCache dimensionCache)
        {
            if (Grouping is IHierarchicalGrouper<TGroup, T> hierarchicalGrouper)
            {
                var groupManager = hierarchicalGrouper.GetPivotGroupManager(subGroup, aggregationFunctions);
                return groupManager;
            }
            return new(Grouping, subGroup, aggregationFunctions);
        }
    }
}