using MeshWeaver.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.Hierarchies;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Grouping;
using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Pivot.Processors
{
    public class DataCubePivotProcessor<TCube, TElement, TIntermediate, TAggregate>
        : PivotProcessorBase<
            TCube,
            DataSlice<TElement>,
            TIntermediate,
            TAggregate,
            DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate>
        >
        where TCube : IDataCube<TElement>
    {
        public DataCubePivotProcessor(
            IPivotConfiguration<TAggregate, ColumnGroup> colConfig,
            IPivotConfiguration<TAggregate, RowGroup> rowConfig,
            DataCubePivotBuilder<TCube, TElement, TIntermediate, TAggregate> pivotBuilder,
            IWorkspace workspace
        )
            : base(pivotBuilder, workspace)
        {
            ColumnConfig = colConfig;
            RowConfig = rowConfig;
        }


        protected override IObservable<DimensionCache> GetStream(IReadOnlyCollection<DataSlice<TElement>> objects)
        {
            var types = 
                PivotBuilder
                    .SliceColumns
                    .Dimensions
                    .Select(d => d.Dim)
                .Concat(
                    PivotBuilder
                    .SliceRows
                    .Dimensions
                    .Select(d => d.Dim)
                    )
                .Distinct()
                    .Select(dim => (Dimension:dim.Type, IdAccessor:(Func<DataSlice<TElement>,object>)(slice => slice.Tuple.GetValue(dim.SystemName))))
                .ToArray();
            return GetStream(objects, types);
        }



        protected override PivotGroupManager<DataSlice<TElement>, TIntermediate, TAggregate, RowGroup>
            GetRowGroupManager(DimensionCache dimensionCache, IReadOnlyCollection<DataSlice<TElement>> transformed)
        {
            var ret = PivotBuilder.SliceRows.GetGroupManager(dimensionCache, PivotBuilder.Aggregations);
            return ret;
        }

        protected override PivotGroupManager<DataSlice<TElement>, TIntermediate, TAggregate, ColumnGroup> GetColumnGroupManager(DimensionCache dimensionCache, IReadOnlyCollection<DataSlice<TElement>> transformed)
        {
            var ret = PivotBuilder.SliceColumns.GetGroupManager(dimensionCache,PivotBuilder.Aggregations);
            return ret;
        }

        public override IObservable<PivotModel> Execute()
        {
            var dimensions = PivotBuilder
                .SliceRows.Dimensions.Select(d => d.Dim)
                .Concat(PivotBuilder.SliceColumns.Dimensions.Select(d => d.Dim))
                .Select(x => x.SystemName)
                .Distinct()
                .ToArray();
            PivotBuilder = PivotBuilder with
            {
                Transformation = cubes => cubes.SelectMany(c => c.GetSlices(dimensions))
            };
            return base.Execute();
        }
    }
}
