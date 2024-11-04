using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Hierarchies;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Grouping;
using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Pivot.Processors;

public abstract class PivotProcessorBase<
    T,
    TTransformed,
    TIntermediate,
    TAggregate,
    TPivotBuilder
>(TPivotBuilder pivotBuilder, IWorkspace workspace)
    where TPivotBuilder : PivotBuilderBase<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >
{
    protected internal IPivotConfiguration<TAggregate, ColumnGroup> ColumnConfig { get; init; }
    protected internal IPivotConfiguration<TAggregate, RowGroup> RowConfig { get; init; }

    protected internal TPivotBuilder PivotBuilder { get; set; } = pivotBuilder;
    protected IWorkspace Workspace { get; } = workspace;

    public virtual IObservable<PivotModel> Execute()
    {
        // transform objects
        var transformed = PivotBuilder.Transformation(PivotBuilder.Objects).ToArray();
        var stream = GetStream(transformed);

        return stream.Select(dimensionCache =>
            {
                var columnGroupManager = GetColumnGroupManager(dimensionCache, transformed);
                var rowGroupManager = GetRowGroupManager(dimensionCache, transformed);
                return EvaluateModel(rowGroupManager, transformed, columnGroupManager);
            })
            ;
    }


    protected abstract IObservable<DimensionCache> GetStream(IReadOnlyCollection<TTransformed> objects);

    protected virtual PivotModel EvaluateModel(
        PivotGroupManager<TTransformed, TIntermediate, TAggregate, RowGroup> rowGroupManager,
        TTransformed[] transformed,
        PivotGroupManager<TTransformed, TIntermediate, TAggregate, ColumnGroup> columnGroupManager)
    {
        // render rows
        var rowGroupings = rowGroupManager
            .CreateRowGroupings(transformed, ImmutableList<string>.Empty)
            .ToList();
        var rows = rowGroupings
            .SelectMany(rowGrouping => GetRowsFromRowGrouping(rowGrouping, columnGroupManager))
            .ToImmutableList();

        // render columns
        var valueColumns = RenderColumnDefinitions().ToList();

        IReadOnlyCollection<Column> columnGroups = columnGroupManager
            .GetColumnGroups(valueColumns)
            .ToList();

        var columns = columnGroups.Any() ? columnGroups : valueColumns;

        // final model
        var model = new PivotModel(columns, rows, rowGroupings.Any());
        return model;
    }

    private IEnumerable<Column> RenderColumnDefinitions()
    {
        return ColumnConfig.GetValueColumns();
    }

    private IReadOnlyCollection<Row> GetRowsFromRowGrouping(
        PivotGrouping<RowGroup, IReadOnlyCollection<TTransformed>> rowGrouping,
        PivotGroupManager<
            TTransformed,
            TIntermediate,
            TAggregate,
            ColumnGroup
        > columnGroupManager
    )
    {
        var objects = rowGrouping.Object.ToArray();
        var rowGroup = rowGrouping.Identity;
        var aggregates = columnGroupManager.GetAggregates(objects, new List<string>());

        var rows =
            RowConfig == null
                ? TransformRowGroupToColumnObject(rowGroup, aggregates)
                : TransformRowGroupToRowObjects(rowGroup, aggregates);

        return rows.ToArray();
    }

    private IEnumerable<Row> TransformRowGroupToRowObjects(
        RowGroup rowGroup,
        HierarchicalRowGroupAggregator<TIntermediate, TAggregate, ColumnGroup> aggregates
    )
    {
        var getAccessors = RowConfig.GetAccessors().ToArray();
        var accessors = GetRowRenderingAccessors(getAccessors);
        var transformedObjects = aggregates.Transform(
            accessors,
            PivotBuilder.Aggregations.ResultTransformation
        );

        var rowDefinitions = getAccessors.Select(a => a.group).ToArray();
        var rows = transformedObjects
            .Select((o, i) => new Row(MergeRows(rowGroup, rowDefinitions[i]), o))
            .ToArray();

        if (rowGroup.Id != IPivotGrouper<T, RowGroup>.TopGroup.Id)
            yield return new Row(rowGroup, null);

        foreach (var row in rows)
            yield return row;
    }

    private static Func<TAggregate, object>[] GetRowRenderingAccessors(
        (RowGroup group, Func<TAggregate, object> accessor)[] getAccessors
    )
    {
        return getAccessors.Select(x => x.accessor).ToArray();
    }

    private IEnumerable<Row> TransformRowGroupToColumnObject(
        RowGroup rowGroup,
        HierarchicalRowGroupAggregator<TIntermediate, TAggregate, ColumnGroup> aggregates
    )
    {
        var getAccessors = ColumnConfig.GetAccessors().ToArray();
        var accessors = GetColumnRenderingAccessors(getAccessors);
        var transformedObjects = aggregates.Transform(
            accessors,
            PivotBuilder.Aggregations.ResultTransformation
        );

        // TODO V10: either check aggregates or return empty inside Render (2021/11/15, Ekaterina Mishina)
        if (transformedObjects == null || !transformedObjects.Any())
            yield break;

        var rowValue = transformedObjects.First();
        yield return new Row(rowGroup, rowValue);
    }

    private Func<TAggregate, object>[] GetColumnRenderingAccessors(
        (ColumnGroup group, Func<TAggregate, object> accessor)[] getAccessors
    )
    {
        var columnRendering =
            (Func<TAggregate, object>)(
                obj => getAccessors.ToDictionary(x => x.group.Id, x => x.accessor(obj))
            );
        var accessors = new[] { columnRendering };
        return accessors;
    }

    private RowGroup MergeRows(RowGroup rowGroup, RowGroup row)
    {
        if (rowGroup.Id == IPivotGrouper<T, RowGroup>.TopGroup.Id)
            return row;
        return row with
        {
            Id = $"{rowGroup.Id}.{row.Id}",
            Coordinates = rowGroup.Coordinates.Concat(row.Coordinates).ToImmutableList(),
        };
    }

    protected abstract PivotGroupManager<TTransformed, TIntermediate, TAggregate, RowGroup> GetRowGroupManager(
        DimensionCache dimensionCache, IReadOnlyCollection<TTransformed> transformed);

    protected abstract PivotGroupManager<TTransformed, TIntermediate, TAggregate, ColumnGroup>
        GetColumnGroupManager(DimensionCache dimensionCache, IReadOnlyCollection<TTransformed> transformed);


    protected IObservable<DimensionCache> GetStream(IReadOnlyCollection<TTransformed> objects, (Type Dimension, Func<TTransformed, object> IdAccessor)[] dimensionProperties)
    {
        var reference = dimensionProperties
            .Select(p => Workspace.DataContext.GetTypeSource(p.Dimension))
            .Where(x => x != null)
            .Distinct()
            .Select(x => x.CollectionName)
            .ToArray();
        var stream = reference.Any()
            ? Workspace.GetStream(new CollectionsReference(reference))
                .Select(x => x.Value)
            : Observable.Return<EntityStore>(new());


        return stream.Select(store =>
        {
            var ret = new DimensionCache(Workspace, store);
            foreach (var dimension in dimensionProperties.Where(x =>
                         typeof(IHierarchicalDimension).IsAssignableFrom(x.Dimension)))
            {
                var hierarchy = ret.GetHierarchy(dimension.Dimension);
                ret.SetMaxHierarchyDataLevel(dimension.Dimension,
                    objects.Select(o => hierarchy.GetNode(dimension.IdAccessor.Invoke(o))?.Level ?? 0).Max());
            }

            return ret;
        });
    }


}
