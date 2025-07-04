using MeshWeaver.DataCubes;
using MeshWeaver.Hierarchies;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Grouping;
using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Builder;

public record SlicePivotGroupingConfigItem<TElement, TGroup>
    : PivotGroupingConfigItem<DataSlice<TElement>, TGroup>
    where TGroup : class, IGroup, new()
{
    public (DimensionDescriptor Dim, PivotGroupBuilder<DataSlice<TElement>, TGroup> GroupBuilder)[] Dimensions { get; }

    public SlicePivotGroupingConfigItem(
        DimensionDescriptor[] dimensions,
        IHierarchicalDimensionOptions hierarchicalDimensionOptions
    )
    {
        Dimensions = dimensions
            .Select(d =>
                (
                    d,
                    GetGroupBuilder(d, hierarchicalDimensionOptions)
                )
            )
            .ToArray();
    }

    public PivotGroupManager<
        DataSlice<TElement>,
        TIntermediate,
        TAggregate,
        TGroup
    > GetGroupManager<TIntermediate, TAggregate>(
        DimensionCache dimensionCache,
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
        >? subGroupManager = null;
        foreach (var tuple in Dimensions.Reverse())
        {
            subGroupManager = CreateSubGroupManager(
                subGroupManager,
                aggregationFunctions,
                tuple.GroupBuilder.GetGrouper(
                    dimensionCache
                )
            );
        }

        return subGroupManager!;

    }


    protected PivotGroupManager<
        DataSlice<TElement>,
        TIntermediate,
        TAggregate,
        TGroup
    > CreateSubGroupManager<TIntermediate, TAggregate>(
        PivotGroupManager<DataSlice<TElement>, TIntermediate, TAggregate, TGroup>? subGroup,
        Aggregations<DataSlice<TElement>, TIntermediate, TAggregate> aggregationFunctions,
        IPivotGrouper<DataSlice<TElement>, TGroup> grouper
    )
    {

        if (grouper is IHierarchicalGrouper<TGroup, DataSlice<TElement>> hierarchicalGrouper)
        {
            // TODO V10: make initialization here for all groupers&managers (2022/01/27, Ekaterina Mishina)
            var groupManager = hierarchicalGrouper.GetPivotGroupManager(
                subGroup!,
                aggregationFunctions
            );
            return groupManager;
        }

        return new(grouper, subGroup!, aggregationFunctions);
    }

    public IPivotGrouper<DataSlice<TElement>, TGroup>? Grouper { get; set; }

    protected PivotGroupBuilder<DataSlice<TElement>, TGroup> GetGroupBuilder(
        DimensionDescriptor dimension,
        IHierarchicalDimensionOptions hierarchicalDimensionOptions
    ) =>
        new(dimensionCache => PivotGroupingExtensions<TGroup>.GetPivotGrouper<TElement>(
            dimensionCache,
            dimension,
            hierarchicalDimensionOptions));

}
