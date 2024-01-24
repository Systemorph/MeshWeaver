using System.Collections.Immutable;
using OpenSmc.Pivot.Builder;
using OpenSmc.Pivot.Grouping;
using OpenSmc.Pivot.Models;

namespace OpenSmc.Pivot.Processors
{
    public abstract class PivotProcessorBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
        where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
    {
        protected internal IPivotConfiguration<TAggregate, ColumnGroup> ColumnConfig { get; init; }
        protected internal IPivotConfiguration<TAggregate, RowGroup> RowConfig { get; init; }

        protected internal TPivotBuilder PivotBuilder { get; set; }

        protected PivotProcessorBase(TPivotBuilder pivotBuilder)
        {
            PivotBuilder = pivotBuilder;
        }

        public virtual PivotModel Execute()
        {
            // transform objects
            var transformed = PivotBuilder.Transformation(PivotBuilder.Objects).ToArray();

            // groupings
            InitializePivotGroupers(PivotBuilder.DimensionsCache, PivotBuilder.HierarchicalDimensionCache, PivotBuilder.HierarchicalDimensionOptions);
            SetMaxLevelForHierarchicalGroupers(transformed);
            var columnGroupManager = GetColumnGroupManager();
            var rowGroupManager = GetRowGroupManager();

            // render rows
            var rowGroupings = rowGroupManager.CreateRowGroupings(transformed, ImmutableList<string>.Empty).ToList();
            var rows = rowGroupings.SelectMany(rowGrouping => GetRowsFromRowGrouping(rowGrouping, columnGroupManager)).ToImmutableList();

            // render columns
            var valueColumns = RenderColumnDefinitions().ToList();

            IReadOnlyCollection<Column> columnGroups = columnGroupManager.GetColumnGroups(valueColumns).ToList();

            var columns = columnGroups.Any() ? columnGroups : valueColumns;

            // final model
            var model = new PivotModel(columns, rows, rowGroupings.Any());
            return model;
        }

        private IEnumerable<Column> RenderColumnDefinitions()
        {
            return ColumnConfig.GetValueColumns();
        }

        private IEnumerable<Row> GetRowsFromRowGrouping(PivotGrouping<RowGroup, ICollection<TTransformed>> rowGrouping, PivotGroupManager<TTransformed, TIntermediate, TAggregate, ColumnGroup> columnGroupManager)
        {
            var objects = rowGrouping.Object.ToArray();
            var rowGroup = rowGrouping.Identity;
            var aggregates = columnGroupManager.GetAggregates(objects, new List<string>());

            var rows = RowConfig == null
                          ? TransformRowGroupToColumnObject(rowGroup, aggregates)
                          : TransformRowGroupToRowObjects(rowGroup, aggregates);

            return rows;
        }

        private IEnumerable<Row> TransformRowGroupToRowObjects(RowGroup rowGroup, HierarchicalRowGroupAggregator<TIntermediate, TAggregate, ColumnGroup> aggregates)
        {
            var getAccessors = RowConfig.GetAccessors().ToArray();
            var accessors = GetRowRenderingAccessors(getAccessors);
            var transformedObjects = aggregates.Transform(accessors, PivotBuilder.Aggregations.ResultTransformation);

            var rowDefinitions = getAccessors.Select(a => a.group).ToArray();
            var rows = transformedObjects.Select((o, i) => new Row(MergeRows(rowGroup, rowDefinitions[i]), o)).ToArray();

            if (rowGroup.SystemName != IPivotGrouper<T, RowGroup>.TopGroup.SystemName)
                yield return new Row(rowGroup, null);

            foreach (var row in rows)
                yield return row;
        }

        private static Func<TAggregate, object>[] GetRowRenderingAccessors((RowGroup group, Func<TAggregate, object> accessor)[] getAccessors)
        {
            return getAccessors.Select(x => x.accessor).ToArray();
        }

        private IEnumerable<Row> TransformRowGroupToColumnObject(RowGroup rowGroup, HierarchicalRowGroupAggregator<TIntermediate, TAggregate, ColumnGroup> aggregates)
        {
            var getAccessors = ColumnConfig.GetAccessors().ToArray();
            var accessors = GetColumnRenderingAccessors(getAccessors);
            var transformedObjects = aggregates.Transform(accessors, PivotBuilder.Aggregations.ResultTransformation);

            // TODO V10: either check aggregates or return empty inside Render (2021/11/15, Ekaterina Mishina)
            if (transformedObjects == null || !transformedObjects.Any())
                yield break;

            var rowValue = transformedObjects.First();
            yield return new Row(rowGroup, rowValue);
        }

        private Func<TAggregate, object>[] GetColumnRenderingAccessors((ColumnGroup group, Func<TAggregate, object> accessor)[] getAccessors)
        {
            var columnRendering = (Func<TAggregate, object>)(obj => getAccessors.ToDictionary(x => x.group.SystemName, x => x.accessor(obj)));
            var accessors = new[] { columnRendering };
            return accessors;
        }

        private RowGroup MergeRows(RowGroup rowGroup, RowGroup row)
        {
            if (rowGroup.SystemName == IPivotGrouper<T, RowGroup>.TopGroup.SystemName)
                return row;
            return row with
                   {
                       SystemName = $"{rowGroup.SystemName}.{row.SystemName}",
                       Coordinates = rowGroup.Coordinates.Concat(row.Coordinates).ToImmutableList(),
                   };
        }

        protected abstract void InitializePivotGroupers(IDimensionCache dimensionCache, IHierarchicalDimensionCache hierarchicalDimensionCache, IHierarchicalDimensionOptions hierarchicalDimensionOptions);
        protected abstract void SetMaxLevelForHierarchicalGroupers(TTransformed[] transformed);
        protected abstract PivotGroupManager<TTransformed, TIntermediate, TAggregate, RowGroup> GetRowGroupManager();
        protected abstract PivotGroupManager<TTransformed, TIntermediate, TAggregate, ColumnGroup> GetColumnGroupManager();
    }
}