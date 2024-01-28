using OpenSmc.DataCubes;
using OpenSmc.Hierarchies;
using OpenSmc.Pivot.Aggregations;
using OpenSmc.Pivot.Grouping;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Builder
{
    public record SlicePivotGroupingConfigItem<TElement, TGroup> : PivotGroupingConfigItem<DataSlice<TElement>, TGroup>
        where TGroup : class, IGroup, new()
    {
        public (DimensionDescriptor Dim, IPivotGrouper<DataSlice<TElement>, TGroup> Grouper)[] Dimensions { get; init; }

        public SlicePivotGroupingConfigItem(DimensionDescriptor[] dimensions)
            : base(default(IPivotGrouper<DataSlice<TElement>, TGroup>))

        {
            Dimensions = dimensions.Select(d => (d, default(IPivotGrouper<DataSlice<TElement>, TGroup>))).ToArray();
        }

        public PivotGroupManager<DataSlice<TElement>, TIntermediate, TAggregate, TGroup> GetGroupManager<TIntermediate, TAggregate>(Aggregations<DataSlice<TElement>, TIntermediate, TAggregate> aggregationFunctions)
        {
            if (Dimensions.Length == 0)
                // TODO V10: should we return here also when we have only __P dimension? (2021/12/15, Ekaterina Mishina)
                return new PivotGroupManager<DataSlice<TElement>, TIntermediate, TAggregate, TGroup>(new DirectPivotGrouper<DataSlice<TElement>, TGroup>(slices =>
                                                                                                                                                             slices.GroupBy(_ => IPivotGrouper<TElement, TGroup>.TopGroup), IPivotGrouper<TElement, TGroup>.TopGroup.GrouperName),
                                                                                                     null,
                                                                                                     aggregationFunctions);

            PivotGroupManager<DataSlice<TElement>, TIntermediate, TAggregate, TGroup> subGroupManager = null;
            foreach (var tuple in Dimensions.Reverse())
            {
                subGroupManager = CreateSubGroupManager(subGroupManager, aggregationFunctions, tuple.Grouper);
            }

            return subGroupManager;
        }

        public void InitializePivotGroupers(IHierarchicalDimensionCache hierarchicalDimensionCache, IDimensionCache dimensionCache, IHierarchicalDimensionOptions hierarchicalDimensionOptions)
        {
            for (int i = 0; i < Dimensions.Length; i++)
            {
                var gr = GetGroupingFunction(Dimensions[i].Dim);
                if (gr is IHierarchicalGrouper<TGroup, DataSlice<TElement>> hgr)
                    hgr.InitializeHierarchies(hierarchicalDimensionCache, hierarchicalDimensionOptions);
                gr.Initialize(dimensionCache);
                Dimensions[i].Grouper = gr;
            }
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

        protected PivotGroupManager<DataSlice<TElement>, TIntermediate, TAggregate, TGroup> CreateSubGroupManager<TIntermediate, TAggregate>(PivotGroupManager<DataSlice<TElement>, TIntermediate, TAggregate, TGroup> subGroup,
                                                                                                                                             Aggregations<DataSlice<TElement>, TIntermediate, TAggregate> aggregationFunctions,
                                                                                                                                             IPivotGrouper<DataSlice<TElement>, TGroup> grouper)
        {
            if (grouper is IHierarchicalGrouper<TGroup, DataSlice<TElement>> hierarchicalGrouper)
            {
                // TODO V10: make initialization here for all groupers&managers (2022/01/27, Ekaterina Mishina)
                var groupManager = hierarchicalGrouper.GetPivotGroupManager(subGroup, aggregationFunctions);
                return groupManager;
            }

            return new(grouper, subGroup, aggregationFunctions);
        }

        protected IPivotGrouper<DataSlice<TElement>, TGroup> GetGroupingFunction(DimensionDescriptor dimension)
        {
            return PivotGroupingExtensions<TGroup>.GetPivotGrouper<TElement>(dimension);
        }
    }
}