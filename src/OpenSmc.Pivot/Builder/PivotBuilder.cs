using System.Linq.Expressions;
using OpenSmc.Pivot.Aggregations;
using OpenSmc.Pivot.Builder.Interfaces;
using OpenSmc.Pivot.Grouping;
using OpenSmc.Pivot.Models;
using OpenSmc.Pivot.Processors;

namespace OpenSmc.Pivot.Builder;

public record PivotBuilder<T, TIntermediate, TAggregate>
    : PivotBuilderBase<T, T, TIntermediate, TAggregate, PivotBuilder<T, TIntermediate, TAggregate>>,
        IPivotBuilder<T, TIntermediate, TAggregate, PivotBuilder<T, TIntermediate, TAggregate>>
{
    public PivotBuilder(IEnumerable<T> objects)
        : base(objects) { }

    private static readonly PivotColumnsGroupingConfiguration<T> DefaultColumnGrouping = new();
    private static readonly PivotRowsGroupingConfiguration<T> DefaultRowsGrouping = new();

    public PivotGroupingConfiguration<T, ColumnGroup> ColumnGroupConfig { get; init; } =
        DefaultColumnGrouping;
    public PivotGroupingConfiguration<T, RowGroup> RowGroupConfig { get; init; } =
        DefaultRowsGrouping;

    public PivotBuilder<T, TIntermediate, TAggregate> GroupRowsBy<TSelected>(
        Expression<Func<T, TSelected>> selector
    )
    {
        return this with { RowGroupConfig = RowGroupConfig.GroupBy(State, selector) };
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
            ColumnGroupConfig = ColumnGroupConfig.GroupBy(State, selector)
        };
    }

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
        new(Objects)
        {
            Aggregations = aggregationsFunc(new Aggregations<T, TIntermediate, TAggregate>()),
        };

    public PivotBuilder<T, TNewAggregate, TNewAggregate> WithAggregation<TNewAggregate>(
        Func<
            Aggregations<T, TIntermediate, TAggregate>,
            Aggregations<T, TNewAggregate>
        > aggregationsFunc
    ) =>
        new(Objects)
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
