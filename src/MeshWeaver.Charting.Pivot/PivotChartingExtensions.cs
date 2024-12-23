using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Charting.Pivot;

public static class PivotChartingExtensions
{
    internal static IPivotBarChartBuilder ToBarChartPivotBuilder<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >(this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        where TPivotBuilder : PivotBuilderBase<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >
    {
        return new PivotBarChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>(
            pivotBuilder
        );
    }

    internal static IPivotLineChartBuilder ToLineChart<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >(this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        where TPivotBuilder : PivotBuilderBase<
                T,
                TTransformed,
                TIntermediate,
                TAggregate,
                TPivotBuilder
            >
    {
        return new PivotLineChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>(
            pivotBuilder
        );
    }

    internal static IPivotRadarChartBuilder ToRadarChartBuilder<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >(this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        where TPivotBuilder : PivotBuilderBase<
                T,
                TTransformed,
                TIntermediate,
                TAggregate,
                TPivotBuilder
            >
    {
        return new PivotRadarChartBuilder<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >(pivotBuilder);
    }

    internal static IPivotWaterfallChartBuilder ToWaterfallChartBuilder<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >(this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        where TPivotBuilder : PivotBuilderBase<
                T,
                TTransformed,
                TIntermediate,
                TAggregate,
                TPivotBuilder
            >
    {
        return new PivotWaterfallChartBuilder<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >(pivotBuilder);
    }

    internal static IPivotWaterfallChartBuilder ToHorizontalWaterfallChartBuilder<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >(this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        where TPivotBuilder : PivotBuilderBase<
                T,
                TTransformed,
                TIntermediate,
                TAggregate,
                TPivotBuilder
            >
    {
        return new PivotHorizontalWaterfallChartBuilder<
            T,
            TTransformed,
            TIntermediate,
            TAggregate,
            TPivotBuilder
        >(pivotBuilder);
    }

    internal static IPivotChartBuilder ToPieChartBuilder<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >(this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        where TPivotBuilder : PivotBuilderBase<
                T,
                TTransformed,
                TIntermediate,
                TAggregate,
                TPivotBuilder
            >
        => new PivotPieChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>(pivotBuilder);

    internal static IPivotChartBuilder ToDoughnutChartBuilder<
        T,
        TTransformed,
        TIntermediate,
        TAggregate,
        TPivotBuilder
    >(this PivotBuilderBase<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder> pivotBuilder)
        where TPivotBuilder : PivotBuilderBase<
                T,
                TTransformed,
                TIntermediate,
                TAggregate,
                TPivotBuilder
            >
        => new PivotDoughnutChartBuilder<T, TTransformed, TIntermediate, TAggregate, TPivotBuilder>(pivotBuilder);
}
