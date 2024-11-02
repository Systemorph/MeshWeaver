using System.Reactive.Linq;
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

        
 
        protected override IObservable<EntityStore> GetStream(IEnumerable<DataSlice<TElement>> objects)
        {
            
            var collections = PivotBuilder.SliceColumns.Dimensions.Select(d => d.Dim.Type)
                .Concat(PivotBuilder.SliceRows.Dimensions.Select(d => d.Dim.Type))
                .Select(Workspace.DataContext.GetTypeSource)
                .Where(x => x != null)
                .Select(t => t.CollectionName)
                .ToArray();


            var stream = Workspace.GetStream(
                new CollectionsReference(collections),
                null
            );

            return stream.Select(x => x.Value);
        }

        protected override PivotModel EvaluateModel(PivotGroupManager<DataSlice<TElement>, TIntermediate, TAggregate, RowGroup> rowGroupManager, DataSlice<TElement>[] transformed,
            PivotGroupManager<DataSlice<TElement>, TIntermediate, TAggregate, ColumnGroup> columnGroupManager)
        {
            SetMaxLevelForHierarchicalGroupers(transformed);
            return base.EvaluateModel(rowGroupManager, transformed, columnGroupManager);
        }

        protected void SetMaxLevelForHierarchicalGroupers(
            IReadOnlyCollection<DataSlice<TElement>> transformed
        )
        {
            PivotBuilder.SliceColumns.UpdateMaxLevelForHierarchicalGroupers(transformed);
            PivotBuilder.SliceRows.UpdateMaxLevelForHierarchicalGroupers(transformed);
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
