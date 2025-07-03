using MeshWeaver.Charting.Models;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Charting.Pivot;

// todo: move to Charting.Layout project (07.08.2024, Alexander Kravets)
public static class PivotChartBuilderExtensions
{
    public static IObservable<ChartModel> ToBarChart<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>(
        this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder,
        Func<IPivotBarChartBuilder, IPivotChartBuilder>? builder = null
    )
        where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
        => pivotBuilder.ToChart(b => b.ToBarChartPivotBuilder(), builder);
    public static IObservable<ChartModel> ToRadarChart<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>(
        this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder,
        Func<IPivotRadarChartBuilder, IPivotChartBuilder>? builder = null
    )
        where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
        => pivotBuilder.ToChart(b => b.ToRadarChartBuilder(), builder);
    public static IObservable<ChartModel> ToWaterfallChart<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>(
        this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder,
        Func<IPivotWaterfallChartBuilder, IPivotChartBuilder>? builder = null
    )
        where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
        => pivotBuilder.ToChart(b => b.ToWaterfallChartBuilder(), builder);
    public static IObservable<ChartModel> ToHorizontalWaterfallChart<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>(
        this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder,
        Func<IPivotWaterfallChartBuilder, IPivotChartBuilder>? builder = null
    )
        where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
        => pivotBuilder.ToChart(b => b.ToHorizontalWaterfallChartBuilder(), builder);

    public static IObservable<ChartModel> ToLineChart<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>(
        this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder,
        Func<IPivotLineChartBuilder, IPivotChartBuilder>? builder = null
    )
        where TPivotBuilder : PivotBuilderBase<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >
        => pivotBuilder.ToChart(b => b.ToLineChart(), builder);

    public static IObservable<ChartModel> ToPieChart<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >(
        this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder,
        Func<IPivotChartBuilder, IPivotChartBuilder>? builder = null
    )
        where TPivotBuilder : PivotBuilderBase<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >
        => pivotBuilder.ToChart(b => b.ToPieChartBuilder(), builder);

    public static IObservable<ChartModel> ToDoughnutChart<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >(
        this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder,
        Func<IPivotChartBuilder, IPivotChartBuilder>? builder = null
    )
        where TPivotBuilder : PivotBuilderBase<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >
        => pivotBuilder.ToChart(b => b.ToDoughnutChartBuilder(), builder);

    private static IObservable<ChartModel> ToChart<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder,
        TPivotChartBuilder>(
        this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder,
        Func<PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>, TPivotChartBuilder>
            pivotChartFactory,
        Func<TPivotChartBuilder, IPivotChartBuilder>? builder = null
    )
        where TPivotBuilder : PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>
        where TPivotChartBuilder : IPivotChartBuilder
    {
        builder ??= x => x;
        return builder(pivotChartFactory(pivotBuilder)).Execute();
    }
}
