using System.Reactive.Linq;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Charting.Pivot;

// todo: move to Charting.Layout project (07.08.2024, Alexander Kravets)
public static class PivotChartBuilderExtensions
{
    public static IObservable<ChartControl> ToBarChart<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >(
        this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder,
        Func<IPivotBarChartBuilder, IPivotBarChartBuilder> builder = null
        )
        where TPivotBuilder : PivotBuilderBase<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >
        => pivotBuilder.ToChart(b => b.ToBarChartPivotBuilder(), builder);

    public static IObservable<ChartControl> ToLineChart<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >(
        this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder,
        Func<IPivotLineChartBuilder, IPivotLineChartBuilder> builder = null
        )
        where TPivotBuilder : PivotBuilderBase<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >
        => pivotBuilder.ToChart(b => b.ToLineChart(), builder);

    public static IObservable<ChartControl> ToPieChart<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >(
        this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder,
        Func<IPivotChartBuilder, IPivotChartBuilder> builder = null
        )
        where TPivotBuilder : PivotBuilderBase<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >
        => pivotBuilder.ToChart(b => b.ToPieChart(), builder);

    public static IObservable<ChartControl> ToDoughnutChart<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >(
        this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder,
        Func<IPivotChartBuilder, IPivotChartBuilder> builder = null
        )
        where TPivotBuilder : PivotBuilderBase<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >
        => pivotBuilder.ToChart(b => b.ToDoughnutChart(), builder);

    private static IObservable<ChartControl> ToChart<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder,
            TPivotChartBuilder
        >(
        this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder,
        Func<PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>, TPivotChartBuilder> pivotChartFactory,
        Func<TPivotChartBuilder, TPivotChartBuilder> builder = null
        )
        where TPivotBuilder : PivotBuilderBase<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >
        where TPivotChartBuilder : IPivotChartBuilder
    {
        var pivotChartBuilder = pivotChartFactory(pivotBuilder);

        if (builder is not null)
        {
            pivotChartBuilder = builder(pivotChartBuilder);
        }

        var chartModel = pivotChartBuilder.Execute();

        return chartModel.Select(x => new ChartControl(x));
    }
}
