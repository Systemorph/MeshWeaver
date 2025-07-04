using System.Reflection;
using AspectCore.Extensions.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Hierarchies;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Pivot.Grouping;
using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Pivot.Processors;

public class PivotProcessor<T, TIntermediate, TAggregate>
    : PivotProcessorBase<
        T,
        T,
        TIntermediate,
        TAggregate,
        PivotBuilder<T, TIntermediate, TAggregate>
    >
{
    public PivotProcessor(
        IPivotConfiguration<TAggregate, ColumnGroup> colConfig,
        IPivotConfiguration<TAggregate, RowGroup>? rowConfig,
        PivotBuilder<T, TIntermediate, TAggregate> pivotBuilder,
        IWorkspace workspace
    )
        : base(pivotBuilder, workspace)
    {
        ColumnConfig = colConfig;
        RowConfig = rowConfig;
    }


    protected override PivotGroupManager<T, TIntermediate, TAggregate, ColumnGroup> GetColumnGroupManager(
        DimensionCache dimensionCache, IReadOnlyCollection<T> transformed)
    {
        PivotGroupManager<T, TIntermediate, TAggregate, ColumnGroup>? columnGroupManager = null;

        foreach (var groupConfig in PivotBuilder.ColumnGroupConfig)
        {
            columnGroupManager = groupConfig.GetGroupManager(dimensionCache, columnGroupManager,
                PivotBuilder.Aggregations
            );
        }

        return columnGroupManager!;
    }

    protected override IObservable<DimensionCache> GetStream(IReadOnlyCollection<T> objects)
    {
        var types = objects.Select(o => o?.GetType()).Distinct().ToArray();
        var dimensionProperties = types
            .Where(t => t != null)
            .SelectMany(t =>
                t!.GetProperties()
                    .Select(p => (Property: p, Dimension: p.GetCustomAttribute<DimensionAttribute>()?.Type )))
            .Where(x => x.Dimension != null)
            .Select(x =>
            {
                var reflector = x.Property.GetReflector();
                return (x.Dimension!, IdAccessor: (Func<T, object>)(e => reflector.GetValue(e) ?? new object()));
            })
            .ToArray();

        return GetStream(objects, dimensionProperties);
    }


    protected override PivotGroupManager<
        T,
        TIntermediate,
        TAggregate,
        RowGroup
    > GetRowGroupManager(DimensionCache dimensionCache, IReadOnlyCollection<T> transformed)
    {
        PivotGroupManager<T, TIntermediate, TAggregate, RowGroup>? rowGroupManager = null;
        foreach (var groupConfig in PivotBuilder.RowGroupConfig)
        {
            rowGroupManager = groupConfig.GetGroupManager(dimensionCache, rowGroupManager,
                PivotBuilder.Aggregations
            );

        }

        return rowGroupManager!;
    }

}
