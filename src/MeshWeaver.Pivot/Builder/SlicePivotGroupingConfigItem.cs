using MeshWeaver.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.Hierarchies;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Grouping;
using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Builder
{
    public record SlicePivotGroupingConfigItem<TElement, TGroup>
        : PivotGroupingConfigItem<DataSlice<TElement>, TGroup>
        where TGroup : class, IGroup, new()
    {
        public (
            DimensionDescriptor Dim,
            IPivotGrouper<DataSlice<TElement>, TGroup> Grouper
        )[] Dimensions { get; }

        public SlicePivotGroupingConfigItem(
            DimensionDescriptor[] dimensions,
            WorkspaceState state,
            IHierarchicalDimensionCache hierarchicalDimensionCache,
            IHierarchicalDimensionOptions hierarchicalDimensionOptions
        )
            : base(default(IPivotGrouper<DataSlice<TElement>, TGroup>)) =>
            Dimensions = dimensions
                .Select(d =>
                    (
                        d,
                        GetGroupingFunction(
                            d,
                            state,
                            hierarchicalDimensionCache,
                            hierarchicalDimensionOptions
                        )
                    )
                )
                .ToArray();

        public PivotGroupManager<
            DataSlice<TElement>,
            TIntermediate,
            TAggregate,
            TGroup
        > GetGroupManager<TIntermediate, TAggregate>(
            Aggregations<DataSlice<TElement>, TIntermediate, TAggregate> aggregationFunctions
        )
        {
            if (Dimensions.Length == 0)
                // TODO V10: should we return here also when we have only __P dimension? (2021/12/15, Ekaterina Mishina)
                return new PivotGroupManager<
                    DataSlice<TElement>,
                    TIntermediate,
                    TAggregate,
                    TGroup
                >(
                    new DirectPivotGrouper<DataSlice<TElement>, TGroup>(
                        slices => slices.GroupBy(_ => IPivotGrouper<TElement, TGroup>.TopGroup),
                        IPivotGrouper<TElement, TGroup>.TopGroup.GrouperName
                    ),
                    null,
                    aggregationFunctions
                );

            PivotGroupManager<
                DataSlice<TElement>,
                TIntermediate,
                TAggregate,
                TGroup
            > subGroupManager = null;
            foreach (var tuple in Dimensions.Reverse())
            {
                subGroupManager = CreateSubGroupManager(
                    subGroupManager,
                    aggregationFunctions,
                    tuple.Grouper
                );
            }

            return subGroupManager;
        }

        public void UpdateMaxLevelForHierarchicalGroupers(
            IReadOnlyCollection<DataSlice<TElement>> transformed
        )
        {
            for (int i = 0; i < Dimensions.Length; i++)
            {
                if (Dimensions[i].Grouper is IHierarchicalGrouper<TGroup, DataSlice<TElement>> hgr)
                    foreach (var element in transformed)
                    {
                        hgr.UpdateMaxLevel(element);
                    }
            }
        }

        protected PivotGroupManager<
            DataSlice<TElement>,
            TIntermediate,
            TAggregate,
            TGroup
        > CreateSubGroupManager<TIntermediate, TAggregate>(
            PivotGroupManager<DataSlice<TElement>, TIntermediate, TAggregate, TGroup> subGroup,
            Aggregations<DataSlice<TElement>, TIntermediate, TAggregate> aggregationFunctions,
            IPivotGrouper<DataSlice<TElement>, TGroup> grouper
        )
        {
            if (grouper is IHierarchicalGrouper<TGroup, DataSlice<TElement>> hierarchicalGrouper)
            {
                // TODO V10: make initialization here for all groupers&managers (2022/01/27, Ekaterina Mishina)
                var groupManager = hierarchicalGrouper.GetPivotGroupManager(
                    subGroup,
                    aggregationFunctions
                );
                return groupManager;
            }

            return new(grouper, subGroup, aggregationFunctions);
        }

        protected IPivotGrouper<DataSlice<TElement>, TGroup> GetGroupingFunction(
            DimensionDescriptor dimension,
            WorkspaceState state,
            IHierarchicalDimensionCache hierarchicalDimensionCache,
            IHierarchicalDimensionOptions hierarchicalDimensionOptions
        )
        {
            return PivotGroupingExtensions<TGroup>.GetPivotGrouper<TElement>(
                dimension,
                state,
                hierarchicalDimensionCache,
                hierarchicalDimensionOptions
            );
        }

        public void UpdateMaxLevelForHierarchicalGroupers(DataSlice<TElement>[] transformed)
        {
            for (int i = 0; i < Dimensions.Length; i++)
            {
                if (Dimensions[i].Grouper is IHierarchicalGrouper<TGroup, DataSlice<TElement>> hgr)
                    foreach (var element in transformed)
                    {
                        hgr.UpdateMaxLevel(element);
                    }
            }
        }
    }
}
