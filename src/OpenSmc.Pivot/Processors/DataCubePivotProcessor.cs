using OpenSmc.DataCubes;
using OpenSmc.Hierarchies;
using OpenSmc.Pivot.Builder;
using OpenSmc.Pivot.Grouping;
using OpenSmc.Pivot.Models;

namespace OpenSmc.Pivot.Processors
{
    public class DataCubePivotProcessor<TCube, TElement, TIntermediate, TAggregate> : PivotProcessorBase<TCube, DataSlice<TElement>, TIntermediate, TAggregate,
                                                                                                             DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate>>
        where TCube : IDataCube<TElement>
    {
        public DataCubePivotProcessor(IPivotConfiguration<TAggregate, ColumnGroup> colConfig,
                                      IPivotConfiguration<TAggregate, RowGroup> rowConfig,
                                      DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate> pivotBuilder)
            : base(pivotBuilder)
        {
            ColumnConfig = colConfig;
            RowConfig = rowConfig;
        }

        protected override PivotGroupManager<DataSlice<TElement>, TIntermediate, TAggregate, ColumnGroup> GetColumnGroupManager()
        {
            var ret = PivotBuilder.SliceColumns.GetGroupManager(PivotBuilder.Aggregations);
            return ret;
        }

        protected override void InitializePivotGroupers(IDimensionCache dimensionCache, IHierarchicalDimensionCache hierarchicalDimensionCache, IHierarchicalDimensionOptions hierarchicalDimensionOptions)
        {
            PivotBuilder.SliceColumns.InitializePivotGroupers(PivotBuilder.HierarchicalDimensionCache, PivotBuilder.DimensionsCache, hierarchicalDimensionOptions);
            PivotBuilder.SliceRows.InitializePivotGroupers(PivotBuilder.HierarchicalDimensionCache, PivotBuilder.DimensionsCache, hierarchicalDimensionOptions);
        }

        protected override void SetMaxLevelForHierarchicalGroupers(DataSlice<TElement>[] transformed)
        {
            PivotBuilder.SliceColumns.UpdateMaxLevelForHierarchicalGroupers(transformed);
            PivotBuilder.SliceRows.UpdateMaxLevelForHierarchicalGroupers(transformed);
        }

        protected override PivotGroupManager<DataSlice<TElement>, TIntermediate, TAggregate, RowGroup> GetRowGroupManager()
        {
            var ret = PivotBuilder.SliceRows.GetGroupManager(PivotBuilder.Aggregations);
            return ret;
        }

        public override PivotModel Execute()
        {
            var dimensions = PivotBuilder.SliceRows.Dimensions.Select(d => d.Dim)
                                         .Concat(PivotBuilder.SliceColumns.Dimensions.Select(d => d.Dim)).Select(x => x.SystemName)
                                         .Distinct()
                                         .ToArray();
            PivotBuilder = PivotBuilder with { Transformation = cubes => cubes.SelectMany(c => c.GetSlices(dimensions)) };
            return base.Execute();
        }
    }
}