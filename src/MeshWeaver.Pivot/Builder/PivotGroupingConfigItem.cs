using MeshWeaver.Hierarchies;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Grouping;
using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Builder
{
    public record PivotGroupingConfigItem<T, TGroup>
        where TGroup : class, IGroup, new()
    {
        private readonly PivotGroupBuilder<T, TGroup>? builder;

        protected PivotGroupingConfigItem() { }

        public PivotGroupingConfigItem(PivotGroupBuilder<T, TGroup> builder)
            => this.builder = builder;

        public virtual PivotGroupManager<T, TIntermediate, TAggregate, TGroup> GetGroupManager<
            TIntermediate,
            TAggregate
        >(
            DimensionCache dimensionCache,
            PivotGroupManager<T, TIntermediate, TAggregate, TGroup> subGroup,
            Aggregations<T, TIntermediate, TAggregate> aggregationFunctions
            )
        {
            if (builder == null)
                throw new InvalidOperationException("Builder is not initialized");

            var grouper = builder.GetGrouper(dimensionCache);
            if (grouper is IHierarchicalGrouper<TGroup, T> hierarchicalGrouper)
            {
                var groupManager = hierarchicalGrouper.GetPivotGroupManager(
                    subGroup,
                    aggregationFunctions
                );
                return groupManager;
            }
            return new(grouper, subGroup, aggregationFunctions);
        }
    }
}
