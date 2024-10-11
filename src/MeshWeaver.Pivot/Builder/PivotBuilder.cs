using System.Linq.Expressions;
using MeshWeaver.Data;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder.Interfaces;
using MeshWeaver.Pivot.Grouping;
using MeshWeaver.Pivot.Models;
using MeshWeaver.Pivot.Processors;

namespace MeshWeaver.Pivot.Builder;

public record PivotBuilder<T, TIntermediate, TAggregate>
    : PivotBuilderBase<T, T, TIntermediate, TAggregate, PivotBuilder<T, TIntermediate, TAggregate>>,
        IPivotBuilder<T, TIntermediate, TAggregate, PivotBuilder<T, TIntermediate, TAggregate>>
{
    public PivotBuilder(IWorkspace workspace, IEnumerable<T> objects)
        : base(workspace, objects)
    {
        ColumnGroupConfig = DefaultColumnGrouping = new PivotColumnsGroupingConfiguration<T>(workspace);
        RowGroupConfig = new PivotRowsGroupingConfiguration<T>(workspace);
    }

    public PivotGroupingConfiguration<T, ColumnGroup> ColumnGroupConfig { get; init; }
    public PivotGroupingConfiguration<T, RowGroup> RowGroupConfig { get; init; }

    public PivotBuilder<T, TIntermediate, TAggregate> GroupRowsBy<TSelected>(
        Expression<Func<T, TSelected>> selector
    )
    {
        return this with
        {
            RowGroupConfig = RowGroupConfig.GroupBy(
                selector,
                HierarchicalDimensionOptions
            )
        };
    }

    public PivotBuilder<T, TIntermediate, TAggregate> GroupColumnsBy<TSelected>(
        Expression<Func<T, TSelected>> selector
    )
    {
        var ret = this;
        if (ColumnGroupConfig == DefaultColumnGrouping)
        {
            ret = ret with { RowGroupConfig = RowGroupConfig.Transpose() };
        }

        return ret with
        {
            ColumnGroupConfig = ColumnGroupConfig.GroupBy(
                selector,
                HierarchicalDimensionOptions
            )
        };
    }

    public PivotGroupingConfiguration<T, ColumnGroup> DefaultColumnGrouping { get; set; }

    public override PivotBuilder<T, TIntermediate, TAggregate> Transpose<TValue>()
    {
        var builder = base.Transpose<TValue>();
        return builder with
        {
            ColumnGroupConfig = ColumnGroupConfig.Transpose(),
            RowGroupConfig = RowGroupConfig.Transpose()
        };
    }

    public PivotBuilder<T, TIntermediate, TAggregate> WithAggregation(
        Func<
            Aggregations<T, TIntermediate, TAggregate>,
            Aggregations<T, TIntermediate, TAggregate>
        > aggregationsFunc
    )
    {
        Aggregations = aggregationsFunc(new Aggregations<T, TIntermediate, TAggregate>());
        return this;
    }

    public PivotBuilder<T, TNewIntermediate, TNewAggregate> WithAggregation<
        TNewIntermediate,
        TNewAggregate
    >(
        Func<
            Aggregations<T, TIntermediate, TAggregate>,
            Aggregations<T, TNewIntermediate, TNewAggregate>
        > aggregationsFunc
    ) =>
        new(Workspace, Objects)
        {
            Aggregations = aggregationsFunc(new Aggregations<T, TIntermediate, TAggregate>()),
        };

    public PivotBuilder<T, TNewAggregate, TNewAggregate> WithAggregation<TNewAggregate>(
        Func<
            Aggregations<T, TIntermediate, TAggregate>,
            Aggregations<T, TNewAggregate>
        > aggregationsFunc
    ) =>
        new(Workspace, Objects)
        {
            Aggregations = aggregationsFunc(new Aggregations<T, TIntermediate, TAggregate>()),
        };

    protected override PivotProcessorBase<
        T,
        T,
        TIntermediate,
        TAggregate,
        PivotBuilder<T, TIntermediate, TAggregate>
    > GetReportProcessor()
    {
        return new PivotProcessor<T, TIntermediate, TAggregate>(
            new PivotColumnsConfiguration<TAggregate>(TransposedValue ?? typeof(TAggregate)),
            TransposedValue != null
                ? new TransposedPivotRowsConfiguration<TAggregate>(TransposedValue)
                : null,
            this
        );
    }
}
